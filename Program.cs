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

    public struct ValuePointer {
        public readonly ulong Address;
        public readonly ClrType Type;

        public ValuePointer (ClrRoot r) {
            // r.Address is the memory location of the root, not the thing it points to
            r.Type.Heap.ReadPointer(r.Address, out Address);
            Type = r.Type;
        }

        public ValuePointer (ulong address, ClrType type) {
            if (type == null)
                throw new ArgumentNullException("type");

            Address = address;
            Type = type;
        }

        public ValuePointer? this[string fieldName] {
            get {
                var field = Type.GetFieldByName(fieldName);
                if (field == null)
                    throw new Exception("No field with this name");

                if (field.IsObjectReference()) {
                    var fieldValue = field.GetFieldValue(Address);
                    if (fieldValue == null)
                        return null;

                    return new ValuePointer((ulong)fieldValue, field.Type);
                } else {
                    var fa = field.GetFieldAddress(Address, false);
                    return new ValuePointer(fa, field.Type);
                }
            }
        }

        public ValuePointer ForceCast (ClrType newType) {
            return new ValuePointer(Address, newType);
        }

        public ValuePointer ForceCast (string newTypeName) {
            var newType = Type.Heap.GetTypeByName(newTypeName);
            if (newType == null)
                throw new Exception("No type with this name");

            return ForceCast(newType);
        }

        public void EnumerateReferences (Action<ulong, int> action) {
            Type.EnumerateRefsOfObjectCarefully(Address, action);
        }

        public ValuePointer? GetSingletonReference (ClrType type) {
            int count = 0;
            object result = null;

            var h = Type.Heap;

            Type.EnumerateRefsOfObjectCarefully(Address, (ulong u, int i) => {
                var t = h.GetObjectType(u);

                if ((t != null) && (t.Index == type.Index)) {
                    Interlocked.Increment(ref count);
                    object _result = new ValuePointer(u, t);
                    Interlocked.Exchange(ref result, _result);
                }
            });

            if (count == 1)
                return (ValuePointer)result;
            else
                return null;
        }

        public object Read () {
            return Type.GetValue(Address);
        }

        public T Read<T> () {
            return (T)Convert.ChangeType(Type.GetValue(Address), typeof(T));
        }

        public override string ToString () {
            return String.Format("<{0:X8} {1}>", Address, Type.Name);
        }
    }

    public class ProcessMangler : IDisposable {
        const uint AttachTimeout = 5000;
        const AttachFlag AttachMode = AttachFlag.Passive;

        public readonly DataTarget DataTarget;
        public readonly ClrRuntime Runtime;
        public readonly ClrHeap Heap;
        public readonly IDebugControl2 Control;

        public ProcessMangler (int processId) {
            DataTarget = DataTarget.AttachToProcess(processId, AttachTimeout, AttachMode);
            var dac = DataTarget.ClrVersions.First().TryGetDacLocation();
            if (dac == null)
                throw new Exception("// Couldn't get DAC location.");
            Runtime = DataTarget.CreateRuntime(dac);

            Heap = Runtime.GetHeap();

            var dr = DataTarget.DataReader;
            var drt = dr.GetType();
            var fi = drt.GetField("m_control", BindingFlags.Instance | BindingFlags.NonPublic);

            if (fi != null) {
                Control = (IDebugControl2)fi.GetValue(dr);
            } else
                Control = null;
        }

        public IEnumerable<ClrRoot> StackLocals {
            get {
                foreach (var thread in Runtime.Threads) {
                    foreach (var r in thread.EnumerateStackObjects())
                        yield return r;
                }
            }
        }

        public IEnumerable<ValuePointer> AllValuesOfType (params ClrType[] types) {
            return AllValuesOfType((IEnumerable<ClrType>)types);
        }

        private IEnumerable<ValuePointer> AllValuesOfType (IEnumerable<ClrType> types) {
            var hs = new HashSet<int>(from t in types select t.Index);

            return from o in Heap.EnumerateObjects()
                   let t = Heap.GetObjectType(o)
                   where hs.Contains(t.Index)
                   select new ValuePointer(o, t);
        }

        public IEnumerable<ValuePointer> AllValuesOfType (params string[] typeNames) {
            return AllValuesOfType(from tn in typeNames select Heap.GetTypeByName(tn));
        }

        public ValuePointer? this[ulong address] {
            get {
                var t = Heap.GetObjectType(address);
                if (t == null)
                    return null;

                return new ValuePointer(address, t);
            }
        }

        public void Dispose () {
            DataTarget.Dispose();
        }
    }
}
