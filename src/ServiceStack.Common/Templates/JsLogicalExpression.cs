﻿using System.Collections.Generic;

namespace ServiceStack.Templates
{
    public class JsLogicalExpression : JsExpression
    {
        public JsLogicOperator Operator { get; set; }
        public JsToken Left { get; set; }
        public JsToken Right { get; set; }
        public override string ToRawString() => "(" + JsonValue(Left) + Operator.Token + JsonValue(Right) + ")";

        public JsLogicalExpression(JsToken left, JsLogicOperator @operator, JsToken right)
        {
            Left = left;
            Operator = @operator;
            Right = right;
        }

        public override object Evaluate(TemplateScopeContext scope)
        {
            var lhs = Left.Evaluate(scope);
            var rhs = Right.Evaluate(scope);
            return Operator.Test(lhs, rhs);
        }

        protected bool Equals(JsLogicalExpression other)
        {
            return Equals(Operator, other.Operator) && Equals(Left, other.Left) && Equals(Right, other.Right);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((JsLogicalExpression) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (Operator != null ? Operator.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Left != null ? Left.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Right != null ? Right.GetHashCode() : 0);
                return hashCode;
            }
        }

        public override Dictionary<string, object> ToJsAst()
        {
            var to = new Dictionary<string, object>
            {
                ["type"] = ToJsAstType(),
                ["operator"] = Operator.Token,
                ["left"] = Left.ToJsAst(),
                ["right"] = Right.ToJsAst(),
            };
            return to;
        }
    }
}