using System;
using System.Collections.Generic;
using System.Text;
using CodeContractsRevival.Runtime;

namespace AskTheCode.SmtLibStandard
{
    /// <summary>
    /// Represents a particular value of the given sort in SMT-LIB.
    /// </summary>
    public sealed class Interpretation : Expression
    {
        public Interpretation(Sort sort, object value)
            : base(ExpressionKind.Interpretation, sort, 0)
        {
            Contract.Requires<ArgumentNullException>(sort != null, nameof(sort));
            Contract.Requires<ArgumentNullException>(value != null, nameof(value));

            this.Value = value;
        }

        public override string DisplayName
        {
            get { return this.Value.ToString(); }
        }

        public object Value { get; private set; }

        public override void Accept(ExpressionVisitor visitor)
        {
            visitor.VisitInterpretation(this);
        }

        public override TResult Accept<TResult>(ExpressionVisitor<TResult> visitor)
        {
            return visitor.VisitInterpretation(this);
        }

        public override Expression GetChild(int index)
        {
            throw new InvalidOperationException();
        }

        protected override void ValidateThis()
        {
            throw new NotImplementedException();
        }
    }
}
