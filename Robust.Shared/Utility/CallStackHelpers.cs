using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using static System.Reflection.BindingFlags;
using static System.Reflection.Emit.OpCodes;

namespace Robust.Shared.Utility
{

    public static class CallStackHelpers
    {

        public const BindingFlags AnyAccess = Public | NonPublic;

        static CallStackHelpers()
        {
            var sfhType = typeof(object).Assembly.GetType("System.Diagnostics.StackFrameHelper")!;
            var shfCtor = sfhType.GetConstructor(AnyAccess | Instance, null, new[] {typeof(Thread)}, null)!;
            var getSackFramesInternal = typeof(StackTrace).GetMethod("GetStackFramesInternal", AnyAccess | Static | FlattenHierarchy)!;
            var getNumberOfFrames = sfhType.GetMethod("GetNumberOfFrames", AnyAccess | Instance | FlattenHierarchy)!;
            var dm = new DynamicMethod("GetThreadCallStackDepth", typeof(int), new Type[] {typeof(Thread)});
            var il = dm.GetILGenerator();
            {
                il.DeclareLocal(sfhType);
                il.DeclareLocal(typeof(int));

                il.Emit(Ldarg_0);
                il.Emit(Newobj, shfCtor);
                il.Emit(Stloc_0);
                il.Emit(Ldloc_0);
                il.Emit(Ldc_I4_0);
                il.Emit(Ldc_I4_0);
                il.Emit(Ldnull);
                il.EmitCall(Call, getSackFramesInternal, null);
                il.Emit(Ldloc_0);
                il.EmitCall(Call, getNumberOfFrames, null);
                il.Emit(Ret);
            }
            _GetThreadCallStackDepth = (Func<Thread?, int>) dm.CreateDelegate(typeof(Func<Thread?, int>));
        }

        private static readonly Func<Thread?, int> _GetThreadCallStackDepth;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int GetCallStackDepth(this Thread thread)
        {
            if (thread == Thread.CurrentThread)
            {
                thread = null!;
            }

            var depth = _GetThreadCallStackDepth(thread);
            if (thread == null)
            {
                --depth;
            }

            return depth;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int GetCallStackDepth()
        {
            var depth = _GetThreadCallStackDepth(null);
            return --depth;
        }

        public static void Init()
        {
            // static constructor will init
        }

    }

}
