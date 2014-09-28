using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace CLRMemoryMangler {
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
}
