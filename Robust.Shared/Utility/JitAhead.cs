using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using Robust.Shared.Interfaces.Log;
using Robust.Shared.Log;
using static System.Reflection.BindingFlags;

namespace Robust.Shared.Utility
{

    public static class JitAhead
    {

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable

        public static Thread Thread { get; }

        private static readonly ISawmill Log = Logger.GetSawmill("aot");

        private static readonly ISet<Type> Already = new HashSet<Type>();

        static JitAhead()
        {
            Thread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                foreach (var asm in AssemblyLoadContext.Default.Assemblies)
                {
                    if (!Validate(asm))
                    {
                        continue;
                    }

                    var methods = 0;
                    foreach (var type in asm.GetTypes())
                    {
                        methods = Handle(type, methods, true);
                    }

                    Log.Info($"Compiled {methods} methods in {asm.GetName().Name}");
                }

                Log.Info($"Finished in {sw.Elapsed.TotalSeconds:F3}s");
            })
            {
                Name = "AOT Thread",
                Priority = ThreadPriority.Lowest,
                IsBackground = true
            };
            Thread.Start();
        }

        private static bool Validate(Assembly asm)
        {
            var asmName = asm.GetName().Name!;
            return asmName.StartsWith("Robust.")
                || asmName.StartsWith("Content.");
        }

        private static int Handle(Type type, int methods, bool prevalidated = false)
        {
            if (!prevalidated && !Validate(type.Assembly))
            {
                return methods;
            }

            if (type.IsGenericTypeDefinition)
            {
                return methods;
            }

            if (CallStackHelpers.GetCallStackDepth() >= 50)
            {
                throw new NotImplementedException("Stack depth exceeds 100");
            }

            //_log.Debug(type.FullName ?? "???");

            foreach (var member in type.GetMembers(DeclaredOnly | NonPublic | Public | Instance | Static))
            {
                switch (member)
                {
                    case FieldInfo fi: methods = HandleConstructedGeneric(fi.FieldType, methods);
                        break;
                    case PropertyInfo pi: methods = HandleConstructedGeneric(pi.PropertyType, methods);
                        break;
                    case Type nt: methods = Handle(nt, methods);
                        break;
                    case EventInfo ei:
                    {
                        var et = ei.EventHandlerType;

                        if (et == null) continue;

                        methods = Handle(et, methods);
                        break;
                    }
                    case MethodBase method
                        when method.IsAbstract || method.IsGenericMethodDefinition:
                        continue;
                    case MethodBase method
                        // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
                        when (method.MethodImplementationFlags & MethodImplAttributes.Native) != 0:
                        continue;
                    case MethodBase method:
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        foreach (var paramInfo in method.GetParameters())
                        {
                            methods = HandleConstructedGeneric(paramInfo.ParameterType, methods);
                        }

                        if (method is MethodInfo mi)
                        {
                            methods = HandleConstructedGeneric(mi.ReturnType, methods);
                        }

                        ++methods;
                        break;
                    }
                }
            }

            return methods;
        }

        private static int HandleConstructedGeneric(Type type, int methods)
        {
            if (!type.IsConstructedGenericType)
            {
                return methods;
            }

            if (CallStackHelpers.GetCallStackDepth() >= 50)
            {
                throw new NotImplementedException("Stack depth exceeds 100");
            }

            if (!Already.Add(type))
            {
                return methods;
            }

            methods = Handle(type, methods);

            return methods;
        }

        public static void Start()
        {
        }

    }

}
