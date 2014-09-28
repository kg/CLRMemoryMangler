using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Interop;

namespace CLRMemoryMangler {
    class Program {
        static ValuePointer? GetScreenArray (ProcessMangler pm, ClrType appType, ClrType listType) {
            try {
                var _game = pm.StackLocals.FirstOrDefault(sl => sl.Type.Index == appType.Index);
                if (_game == null)
                    return null;
                var game = new ValuePointer(_game);
                var screenManager = game["<ScreenManager>k__BackingField"].Value;
                var screens = screenManager["screens"].Value.ForceCast(listType);
                return screens["_items"].Value;
            } catch {
                return null;
            }
        }

        static void Main (string[] args) {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            using (var pm = AttachToProcess("bastion")) {
                var primaryAppDomain = pm.Runtime.AppDomains.First();
                var tApp = pm.Heap.GetTypeByName("GSGE.App");
                var tList = 
                    // CLR 4.x
                    pm.Heap.GetTypeByName("System.Collections.Generic.List<T>")
                    ?? 
                    // CLR 2.x
                    pm.Heap.GetTypeByName("System.Collections.Generic.List`1");
                var tWorld = pm.Heap.GetTypeByName("GSGE.World");
                var fWorldState = tWorld.GetStaticFieldByName("m_state");
                var tWorldState = pm.Heap.GetTypeByName("GSGE.World+State");
                var fMapName = tWorldState.GetFieldByName("<MapName>k__BackingField");

                if (fWorldState.GetFieldAddress(primaryAppDomain) == 0)
                    Console.WriteLine("Static fields not accessible. Make sure Bastion is running under CLR 4.0, not 2.0.");

                var tLoadScreen = pm.Heap.GetTypeByName("GSGE.Code.GUI.LoadScreen");

                if (pm.Control != null) {
                    pm.Control.SetExecutionStatus(DEBUG_STATUS.GO);
                    pm.Control.WaitForEvent(DEBUG_WAIT.DEFAULT, 5000);
                }

                int wasLoading = 0;
                string previousMapName = null;
                while (true) {
                    while (!pm.Heap.CanWalkHeap)
                        Thread.Sleep(0);

                    int isLoading = 0;

                    var screenItems = GetScreenArray(pm, tApp, tList);

                    if (screenItems.HasValue) {
                        try {
                            screenItems.Value.EnumerateReferences((u, i) => {
                                var _v = pm[u];
                                if (!_v.HasValue)
                                    return;

                                var t = _v.Value.Type;

                                if (t.Index == tLoadScreen.Index)
                                    Interlocked.Add(ref isLoading, 1);
                            });

                            if (isLoading != wasLoading) {
                                wasLoading = isLoading;
                                Console.WriteLine("Now {0}", (isLoading == 1) ? "loading" : "playing");
                            }
                        } catch {
                        }
                    }

                    var worldState = fWorldState.GetFieldValue(primaryAppDomain);
                    if (worldState != null) {
                        var mapName = (string)fMapName.GetFieldValue((ulong)worldState);

                        if (mapName != previousMapName) {
                            Console.WriteLine("Map {0} -> {1}", previousMapName, mapName);
                            previousMapName = mapName;
                        }
                    }

                    Thread.Sleep(50);
                }
            }

            if (Debugger.IsAttached)
                Console.ReadLine();
        }

        static ProcessMangler AttachToProcess (string processName) {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Count() > 1) {
                throw new Exception("Multiple processes found with name '" + processName + "'");
            } else if (!processes.Any()) {
                throw new Exception("No process found with name '" + processName + "'");
            }

            var proc = processes.First();
            int id = proc.Id;
            proc.Dispose();
            return new ProcessMangler(id);
        }
    }
}
