using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// Nezia ネイティブ層から返された <see cref="NeziaResult"/> を例外として表現する。
    /// </summary>
    public sealed class NeziaException : Exception
    {
        public NeziaErrorCode Code { get; }

        internal NeziaException(NeziaResult result, string message)
            : base($"[Nezia] {message} (code: {result})")
        {
            Code = (NeziaErrorCode)(int)result;
        }

        internal static void ThrowIfError(NeziaResult result, string operation)
        {
            if (result == NeziaResult.Ok) return;
            throw new NeziaException(result, operation);
        }
    }

    /// <summary>
    /// <see cref="NeziaResult"/> の公開ミラー。FFI 型は internal なので公開用に再定義する。
    /// </summary>
    public enum NeziaErrorCode
    {
        Ok = 0,
        NullPointer = -1,
        InvalidHandle = -2,
        QueueFull = -3,
        IoError = -4,
        DecodeError = -5,
        BusLoopDetected = -6,
        InvalidArgument = -7,
        Panic = -100,
        InternalError = -101,
    }
}
