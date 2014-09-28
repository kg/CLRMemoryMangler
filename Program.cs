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
        static ArrayPointer? GetScreenArray (ValuePointer app, ClrType listType, ClrType screenType) {
            var screenManager = app["<ScreenManager>k__BackingField"];
            if (!screenManager.HasValue)
                return null;

            var screens = screenManager.Value["screens"];
            if (!screens.HasValue)
                return null;

            var screensList = screens.Value.ForceCast(listType);
            var screensArray = screensList["_items"].Value.ForceCast("System.Object[]");
            return new ArrayPointer(screensArray, screenType);
        }

        static void Main (string[] args) {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            using (var pm = AttachToProcess("bastion")) {
                var fApp = new StaticField(pm.Runtime, "GSGE.App", "SingletonApp");

                var tList = 
                    // CLR 4.x
                    pm.Heap.GetTypeByName("System.Collections.Generic.List<T>")
                    ?? 
                    // CLR 2.x
                    pm.Heap.GetTypeByName("System.Collections.Generic.List`1");

                var fWorldState = new StaticField(pm.Runtime, "GSGE.World", "m_state");
                var fMapName = fWorldState.Type.GetFieldByName("<MapName>k__BackingField");

                var tLoadScreen = pm.Heap.GetTypeByName("GSGE.Code.GUI.LoadScreen");
                var tScreen = pm.Heap.GetTypeByName("GSGE.GameScreen");

                if (!fWorldState.IsAccessible)
                    Console.WriteLine("Static fields not accessible. Make sure Bastion is running under CLR 4.0, not 2.0.");

                int wasLoading = 0;
                string previousMapName = null;
                while (true) {
                    while (!pm.Heap.CanWalkHeap)
                        Thread.Sleep(0);

                    int isLoading = 0;

                    // Find the app singleton in a static local
                    var app = fApp.Value;
                    if (app != null) {
                        // Now pull out the screen array from the app's screenmanager
                        var screenItems = GetScreenArray(app.Value, tList, tScreen);

                        if (screenItems.HasValue) {
                            // Scan through all the active screens to find a loading screen
                            for (int i = 0; i < screenItems.Value.Count; i++) {
                                var screen = screenItems.Value[i];
                                
                                // Null so we're at the end of the list
                                if (!screen.HasValue)
                                    break;

                                // Found one
                                if (screen.Value.Type.Index == tLoadScreen.Index) {
                                    Interlocked.Add(ref isLoading, 1);
                                    break;
                                }
                            }

                            if (isLoading != wasLoading) {
                                wasLoading = isLoading;
                                Console.WriteLine("Now {0}", (isLoading == 1) ? "loading" : "playing");
                            }
                        }
                    }

                    // Find the static field containing the current world state
                    var worldState = fWorldState.Value;
                    if (worldState != null) {
                        // Figure out the map name
                        var mapName = (string)fMapName.GetFieldValue(worldState.Value.Address);

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
