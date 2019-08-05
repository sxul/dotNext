﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;
using static InlineIL.IL;
using static InlineIL.IL.Emit;

namespace DotNext.Threading
{
    using static Runtime.InteropServices.Memory;

    internal static class Atomic
    {
        //T should not be greater than maximum size of primitive type. For .NET Standard it is sizeof(long)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T Read<T>(ref T value)
        {
            Push(ref value);
            Volatile();
            Ldobj(typeof(T));
            return Return<T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Write<T>(ref T storage, T value)
        {
            Push(ref storage);
            Push(value);
            Volatile();
            Stobj(typeof(T));
            Ret();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Equals<T>(T x, T y)
        {
            Push(x);
            Push(y);
            Ceq();
            return Return<bool>();
        }
    }

    /// <summary>
    /// Provides volatile access to non-primitive data type.
    /// </summary>
    /// <remarks>
    /// Synchronized methods can be declared in classes only. If you don't need to have extra heap allocation
    /// to keep synchronization root in the form of the object or you need to have volatile field
    /// inside of value type then <c>VolatileContainer</c> is the best choice. Its performance is better
    /// than synchronized methods according with benchmarks.
    /// </remarks>
    public struct Atomic<T> : IStrongBox, ICloneable
        where T : struct
    {
        /// <summary>
        /// Represents atomic update action.
        /// </summary>
        /// <remarks>The atomic update action should side-effect free.</remarks>
        /// <param name="current">The value to update.</param>
        /// <param name="newValue">The updated value.</param>
        public delegate void Updater(in T current, out T newValue);

        /// <summary>
        /// Represents atomic accumulator.
        /// </summary>
        /// <remarks>The atomic accumulator should side-effect free.</remarks>
        /// <param name="current">The value to update.</param>
        /// <param name="x">The supplied value to be combined with the </param>
        /// <param name="newValue">The accumulated value.</param>
        public delegate void Accumulator(in T current, in T x, out T newValue);

        private T value;

        private AtomicBoolean lockState;

        private void Lock()
        {
            for (SpinWait spinner; lockState.CompareExchange(true, false); spinner.SpinOnce()) { }
        }

        private void Unlock() => lockState.Value = false;

        /// <summary>
        /// Clones thic container atomically.
        /// </summary>
        /// <param name="container">The memory location used to store cloned container.</param>
        public void Clone(out Atomic<T> container)
        {
            Lock();
            Copy(this, out container);
            Unlock();
        }

        object ICloneable.Clone()
        {
            Clone(out var container);
            return container;
        }

        /// <summary>
        /// Performs atomic read.
        /// </summary>
        /// <param name="result">The result of volatile read.</param>
        public void Read(out T result)
        {
            Lock();
            Copy(in value, out result);
            Unlock();
        }

        /// <summary>
        /// Swaps the value stored in this container and the given value atomically.
        /// </summary>
        /// <remarks>
        /// This operation is atomic for both containers.
        /// </remarks>
        /// <param name="other">The container for the value.</param>
        public void Swap(ref Atomic<T> other)
        {
            Lock();
            Swap(ref other.value);
            Unlock();
        }

        /// <summary>
        /// Swaps the value stored in this container and the given value atomically.
        /// </summary>
        /// <param name="other">The managed pointer to the value to swap.</param>
        public void Swap(ref T other)
        {
            Lock();
            Runtime.InteropServices.Memory.Swap(ref value, ref other);
            Unlock();
        }

        /// <summary>
        /// Performs atomic write.
        /// </summary>
        /// <param name="newValue">The value to be stored into this container.</param>
        public void Write(in T newValue)
        {
            Lock();
            Copy(in newValue, out value);
            Unlock();
        }

        /// <summary>
        /// Compares two values of type <typeparamref name="T"/> for bitwise equality and, if they are equal, replaces the stored value.
        /// </summary>
        /// <param name="update">The value that replaces the stored value if the comparison results in equality.</param>
        /// <param name="expected">The value that is compared to the stored value.</param>
        /// <param name="result">The origin value stored in this container before modification.</param>
        public void CompareExchange(in T update, in T expected, out T result)
        {
            Lock();
            var current = value;
            if (ValueType<T>.BitwiseEquals(current, expected))
                Copy(in update, out value);
            Copy(in current, out result);
            Unlock();
        }

        /// <summary>
        /// Atomically sets the stored value to the given updated value if the current value == the expected value.
        /// </summary>
        /// <param name="expected">The expected value.</param>
        /// <param name="update">The new value.</param>
        /// <returns><see langword="true"/> if successful. <see langword="false"/> return indicates that the actual value was not equal to the expected value.</returns>
        public bool CompareAndSet(in T expected, in T update)
        {
            Lock();
            bool result;
            if (result = ValueType<T>.BitwiseEquals(value, expected))
                Copy(in update, out value);
            Unlock();
            return result;
        }

        /// <summary>
        /// Sets a value stored in this container to a specified value and returns the original value, as an atomic operation.
        /// </summary>
        /// <param name="update">The value that replaces the stored value.</param>
        /// <param name="previous">The original stored value before modification.</param>
        public void Exchange(in T update, out T previous)
        {
            Lock();
            Copy(in value, out previous);
            Copy(in update, out value);
            Unlock();
        }

        private void Update(Updater updater, out T result, bool newValueExpected)
        {
            T newValue, oldValue;
            do
            {
                Read(out oldValue);
                updater(oldValue, out newValue);
            }
            while (!CompareAndSet(in oldValue, in newValue));
            result = newValueExpected ? newValue : oldValue;
        }

        /// <summary>
        /// Atomically updates the stored value with the results of applying the given function, returning the updated value.
        /// </summary>
        /// <param name="updater">A side-effect-free function</param>
        /// <param name="result">The updated value.</param>
        public void UpdateAndGet(Updater updater, out T result)
            => Update(updater, out result, true);

        /// <summary>
        /// Atomically updates the stored value with the results 
        /// of applying the given function, returning the original value.
        /// </summary>
        /// <param name="updater">A side-effect-free function</param>
        /// <param name="result">The original value.</param>
        public void GetAndUpdate(Updater updater, out T result)
            => Update(updater, out result, false);

        private void Accumulate(in T x, Accumulator accumulator, out T result, bool newValueExpected)
        {
            T oldValue, newValue;
            do
            {
                Read(out oldValue);
                accumulator(oldValue, x, out newValue);
            }
            while (!CompareAndSet(oldValue, newValue));
            result = newValueExpected ? newValue : oldValue;
        }

        /// <summary>
        /// Atomically updates the stored value with the results of applying the given function 
        /// to the current and given values, returning the original value.
        /// </summary>
        /// <remarks>
        /// The function is applied with the current value as its first argument, and the given update as the second argument.
        /// </remarks>
        /// <param name="x">Accumulator operand.</param>
        /// <param name="accumulator">A side-effect-free function of two arguments</param>
        /// <param name="result">The updated value.</param>
        public void AccumulateAndGet(in T x, Accumulator accumulator, out T result)
            => Accumulate(x, accumulator, out result, true);

        /// <summary>
        /// Atomically updates the stored value with the results of applying the given function 
        /// to the current and given values, returning the updated value.
        /// </summary>
        /// <remarks>
        /// The function is applied with the current value as its first argument, and the given update as the second argument.
        /// </remarks>
        /// <param name="x">Accumulator operand.</param>
        /// <param name="accumulator">A side-effect-free function of two arguments</param>
        /// <param name="result">The original value.</param>
        public void GetAndAccumulate(in T x, Accumulator accumulator, out T result)
            => Accumulate(x, accumulator, out result, false);

        /// <summary>
        /// Gets or sets value in volatile manner.
        /// </summary>
        /// <remarks>
        /// To achieve best performance it is recommended to use <see cref="Read"/> and <see cref="Write"/> methods
        /// because they don't cause extra allocation of stack memory for passing value.
        /// </remarks>
        public T Value
        {
            get
            {
                Read(out var result);
                return result;
            }
            set => Write(value);
        }

        object IStrongBox.Value
        {
            get => Value;
            set => Value = (T)value;
        }

        /// <summary>
        /// Converts the stored value into string atomically.
        /// </summary>
        /// <returns>The string returned from <see cref="object.ToString"/> method called on the stored value.</returns>
        public override string ToString()
        {
            Read(out var result);
            return result.ToString();
        }
    }
}
