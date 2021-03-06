﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AskTheCode.ControlFlowGraphs.Operations
{
    /// <summary>
    /// Represents an operation within an <see cref="InnerFlowNode"/>.
    /// </summary>
    public abstract class Operation
    {
        public abstract void Accept(OperationVisitor visitor);

        public abstract TResult Accept<TResult>(OperationVisitor<TResult> visitor);
    }
}
