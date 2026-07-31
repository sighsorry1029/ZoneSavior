using System;
using System.Collections;
using System.Collections.Generic;

namespace ZoneSavior;

internal static class ZoneSaviorCoroutines
{
    public static IEnumerator RunSafely(IEnumerator operation, Action<Exception?> onComplete)
    {
        Stack<IEnumerator> coroutineStack = [];
        coroutineStack.Push(operation);
        Exception? failure = null;
        try
        {
            while (coroutineStack.Count > 0)
            {
                IEnumerator coroutine = coroutineStack.Peek();
                bool hasNext = false;
                object? current = null;
                try
                {
                    hasNext = coroutine.MoveNext();
                    if (hasNext)
                    {
                        current = coroutine.Current;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (failure != null)
                {
                    break;
                }

                if (!hasNext)
                {
                    coroutineStack.Pop();
                    try
                    {
                        (coroutine as IDisposable)?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    continue;
                }

                if (current is IEnumerator nested)
                {
                    coroutineStack.Push(nested);
                    continue;
                }

                yield return current;
            }
        }
        finally
        {
            while (coroutineStack.Count > 0)
            {
                try
                {
                    (coroutineStack.Pop() as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            }

            onComplete(failure);
        }
    }
}
