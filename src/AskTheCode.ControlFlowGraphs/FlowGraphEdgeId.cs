using System;
using System.Collections.Generic;
using System.Text;
using AskTheCode.Common;

namespace AskTheCode.ControlFlowGraphs
{
    public struct FlowGraphEdgeId : IOrdinalId<FlowGraphEdgeId>
    {
        private readonly int value;

#if DEBUG
        private readonly bool isValid;
#endif

        public FlowGraphEdgeId(int value)
        {
            this.value = value;

#if DEBUG
            this.isValid = true;
#endif
        }

        public bool IsValid
        {
            get
            {
#if DEBUG
                return this.isValid;
#else
                return true;
#endif
            }
        }

        public int Value
        {
            get { return this.value; }
        }

        public bool Equals(FlowGraphEdgeId other)
        {
            return this.Value == other.Value;
        }

        internal class Provider : IIdProvider<FlowGraphEdgeId>
        {
            private OrdinalIdValueGenerator valueGenerator = new OrdinalIdValueGenerator();

            public FlowGraphEdgeId GenerateNewId()
            {
                int id = this.valueGenerator.GenerateNextIdValue();
                return new FlowGraphEdgeId(id);
            }
        }
    }
}
