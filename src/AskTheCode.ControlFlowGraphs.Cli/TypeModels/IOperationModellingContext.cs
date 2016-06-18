﻿using System;
using AskTheCode.SmtLibStandard;
using AskTheCode.SmtLibStandard.Handles;

namespace AskTheCode.ControlFlowGraphs.Cli.TypeModels
{
    public interface IOperationModellingContext
    {
        void AddExceptionThrow(BoolHandle condition, Type exceptionType);

        void AddAssignment(Variable variable, Expression value);

        void SetValue(Expression expression);

        // TODO: Enable to specify the exception constructor (with arguments) and use also ITypeSymbol
        //       (it might be useful in case of uncommon exceptions)
    }
}