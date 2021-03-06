using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ServiceStack.Text;

#if NETSTANDARD2_0
using Microsoft.Extensions.Primitives;
#endif

namespace ServiceStack.Templates
{
    public class RawString : IRawString
    {
        public static RawString Empty = new RawString("");
        
        private readonly string value;
        public RawString(string value) => this.value = value;
        public string ToRawString() => value;
    }

    public static class TemplatePageUtils
    {
        public static List<PageFragment> ParseTemplatePage(string text)
        {
            return ParseTemplatePage(new StringSegment(text));
        }

        private const char FilterSep = '|';

        // {{#name}}  {{else if a=b}}  {{else}}  {{/name}}
        //          ^
        // returns    ^                         ^
        static StringSegment ParseStatementBody(this StringSegment literal, StringSegment blockName, out List<PageFragment> body)
        {
            var inStatements = 0;
            var pos = 0;
            
            while (true)
            {
                pos = literal.IndexOf("{{", pos);
                if (pos == -1)
                    throw new SyntaxErrorException($"End block for '{blockName}' not found.");

                var c = literal.SafeGetChar(pos + 2);

                if (c == '#')
                {
                    inStatements++;
                    pos = literal.IndexOf("}}", pos) + 2; //end of expression
                    continue;
                }

                if (c == '/')
                {
                    if (inStatements == 0)
                    {
                        literal.Subsegment(pos + 2 + 1).ParseVarName(out var name);
                        if (name == blockName)
                        {
                            body = ParseTemplatePage(literal.Subsegment(0, pos).TrimFirstNewLine());
                            return literal.Subsegment(pos);
                        }
                    }

                    inStatements--;
                }
                else if (literal.Subsegment(pos + 2).StartsWith("else"))
                {
                    if (inStatements == 0)
                    {
                        body = ParseTemplatePage(literal.Subsegment(0, pos).TrimFirstNewLine());
                        return literal.Subsegment(pos);
                    }
                }

                pos += 2;
            }
        }

        //   {{else if a=b}}  {{else}}  {{/name}}
        //  ^
        // returns           ^         ^
        static StringSegment ParseElseStatement(this StringSegment literal, StringSegment blockName, out PageElseBlock statement)
        {
            var inStatements = 0;
            var pos = 0;
            statement = null;
            var statementPos = -1;
            var elseExpr = default(StringSegment);
            
            while (true)
            {
                pos = literal.IndexOf("{{", pos);
                if (pos == -1)
                    throw new SyntaxErrorException($"End block for 'else' not found.");

                var c = literal.SafeGetChar(pos + 2);
                if (c == '#')
                {
                    inStatements++;
                    pos = literal.IndexOf("}}", pos) + 2; //end of expression                    
                }
                else if (c == '/')
                {
                    if (inStatements == 0)
                    {
                        literal.Subsegment(pos + 2 + 1).ParseVarName(out var name);
                        if (name == blockName)
                        {
                            var body = ParseTemplatePage(literal.Subsegment(statementPos, pos - statementPos).TrimFirstNewLine());
                            statement = new PageElseBlock(elseExpr, body);
                            return literal.Subsegment(pos);
                        }
                    }

                    inStatements--;
                }
                else if (literal.Subsegment(pos + 2).StartsWith("else"))
                {
                    if (inStatements == 0)
                    {
                        if (statementPos >= 0)
                        {
                            var bodyText = literal.Subsegment(statementPos, pos - statementPos).TrimFirstNewLine();
                            var body = ParseTemplatePage(bodyText);
                            statement = new PageElseBlock(elseExpr, body);
                            return literal.Subsegment(pos);
                        }
                        
                        var endExprPos = literal.IndexOf("}}", pos);
                        if (endExprPos == -1)
                            throw new SyntaxErrorException($"End expression for 'else' not found.");

                        var exprStartPos = pos + 2 + 4; //= {{else...

                        elseExpr = literal.Subsegment(exprStartPos, endExprPos - exprStartPos).Trim();
                        statementPos = endExprPos + 2;
                    }
                }

                pos += 2;
            }
        }

        public static List<PageFragment> ParseTemplatePage(StringSegment text)
        {
            var to = new List<PageFragment>();

            if (text.IsNullOrWhiteSpace())
                return to;
            
            int pos;
            var lastPos = 0;
            while ((pos = text.IndexOf("{{", lastPos)) != -1)
            {
                var block = text.Subsegment(lastPos, pos - lastPos);
                if (!block.IsNullOrEmpty())
                    to.Add(new PageStringFragment(block));
                
                var varStartPos = pos + 2;

                var firstChar = text.GetChar(varStartPos);
                if (firstChar == '*') //comment
                {
                    lastPos = text.IndexOf("*}}", varStartPos) + 3;
                }
                else if (firstChar == '#') //block statement
                {
                    var literal = text.Subsegment(varStartPos + 1);
                    literal = literal.ParseVarName(out var blockName);

                    var endExprPos = literal.IndexOf("}}");
                    if (endExprPos == -1)
                        throw new SyntaxErrorException($"Unterminated '{blockName}' block expression, near '{literal.DebugLiteral()}'" );

                    var blockExpr = literal.Subsegment(0, endExprPos).Trim();
                    literal = literal.Advance(endExprPos + 2);

                    var blockNameString = blockName.Value;
                    if (!TemplateConfig.BlockNamessWithStringBody.Contains(blockNameString))
                    {
                        literal = literal.ParseStatementBody(blockName, out var body);
                        var elseStatements = new List<PageElseBlock>();

                        while (literal.StartsWith("{{else"))
                        {
                            literal = literal.ParseElseStatement(blockName, out var elseStatement);
                            elseStatements.Add(elseStatement);
                        }

                        literal = literal.Advance(2 + 1 + blockName.Length + 2);
                    
                        //remove new line after partial block end tag
                        literal = literal.TrimFirstNewLine();

                        var length = text.Length - pos - literal.Length;
                        var originalText = text.Subsegment(pos, length);
                        lastPos = pos + length;
                    
                        var statement = new PageBlockFragment(originalText, blockName, blockExpr, body, elseStatements);
                        to.Add(statement);
                    }
                    else
                    {
                        var endBlock = "{{/" + blockNameString + "}}";
                        var endBlockPos = literal.IndexOf(endBlock);
                        if (endBlockPos == -1)
                            throw new SyntaxErrorException($"Unterminated end block '{endBlock}'");

                        var endBlockBody = literal.Subsegment(0, endBlockPos);
                        literal = literal.Advance(endBlockPos + endBlock.Length).TrimFirstNewLine();
                        var body = new List<PageFragment>{ new PageStringFragment(endBlockBody) };
                        
                        var length = text.Length - pos - literal.Length;
                        var originalText = text.Subsegment(pos, length);
                        lastPos = pos + length;
                    
                        var statement = new PageBlockFragment(originalText, blockName, blockExpr, body);
                        to.Add(statement);
                    }
                }
                else
                {
                    var literal = text.Subsegment(varStartPos);
                    literal = literal.ParseJsExpression(out var expr, filterExpression: true);

                    var filters = new List<JsCallExpression>();

                    if (!literal.StartsWith("}}"))
                    {
                        literal = literal.AdvancePastWhitespace();
                        if (literal.FirstCharEquals(FilterSep))
                        {
                            literal = literal.Advance(1);

                            while (true)
                            {
                                literal = literal.ParseJsCallExpression(out var filter, filterExpression: true);

                                filters.Add(filter);

                                literal = literal.AdvancePastWhitespace();

                                if (literal.IsNullOrEmpty())
                                    throw new SyntaxErrorException("Unterminated filter expression");

                                if (literal.StartsWith("}}"))
                                {
                                    literal = literal.Advance(2);
                                    break;
                                }

                                if (!literal.FirstCharEquals(FilterSep))
                                    throw new SyntaxErrorException(
                                        $"Expected filter separator '|' but was {literal.DebugFirstChar()}");

                                literal = literal.Advance(1);
                            }
                        }
                        else
                        {
                            if (!literal.IsNullOrEmpty())
                                literal = literal.Advance(1);
                        }
                    }
                    else
                    {
                        literal = literal.Advance(2);
                    }

                    var length = text.Length - pos - literal.Length;
                    var originalText = text.Subsegment(pos, length);
                    lastPos = pos + length;

                    var varFragment = new PageVariableFragment(originalText, expr, filters);
                    to.Add(varFragment);

                    var newLineLen = literal.StartsWith("\n")
                        ? 1
                        : literal.StartsWith("\r\n")
                            ? 2
                            : 0;

                    if (newLineLen > 0)
                    {
                        var lastExpr = varFragment.FilterExpressions?.LastOrDefault();
                        var filterName = lastExpr?.Name ??
                                         varFragment?.InitialExpression?.Name ?? varFragment.BindingString;
                        if (filterName != null && TemplateConfig.RemoveNewLineAfterFiltersNamed.Contains(filterName))
                        {
                            lastPos += newLineLen;
                        }
                    }
                }
            }

            if (lastPos != text.Length)
            {
                var lastBlock = lastPos == 0 ? text : text.Subsegment(lastPos);
                to.Add(new PageStringFragment(lastBlock));
            }

            return to;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IRawString ToRawString(this string value) => 
            new RawString(value ?? "");
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IRawString ToRawString(this StringSegment value) => 
            new RawString(value.HasValue ? value.Value : "");

        public static ConcurrentDictionary<string, Func<TemplateScopeContext, object, object>> BinderCache { get; } = new ConcurrentDictionary<string, Func<TemplateScopeContext, object, object>>();

        public static Func<TemplateScopeContext, object, object> GetMemberExpression(Type targetType, StringSegment expression)
        {
            if (targetType == null)
                throw new ArgumentNullException(nameof(targetType));
            if (expression.IsNullOrWhiteSpace())
                throw new ArgumentNullException(nameof(expression));

            var key = targetType.FullName + ':' + expression;

            if (BinderCache.TryGetValue(key, out var fn))
                return fn;

            BinderCache[key] = fn = Compile(targetType, expression);

            return fn;
        }
        
        public static Func<TemplateScopeContext, object, object> Compile(Type type, StringSegment expr)
        {
            var scope = Expression.Parameter(typeof(TemplateScopeContext), "scope");
            var param = Expression.Parameter(typeof(object), "instance");
            var body = CreateBindingExpression(type, expr, scope, param);

            body = Expression.Convert(body, typeof(object));
            return Expression.Lambda<Func<TemplateScopeContext, object, object>>(body, scope, param).Compile();
        }

        private static Expression CreateBindingExpression(Type type, StringSegment expr, ParameterExpression scope, ParameterExpression instance)
        {
            Expression body = Expression.Convert(instance, type);

            var currType = type;

            var pos = 0;
            var depth = 0;
            while (expr.TryReadPart(".", out StringSegment member, ref pos))
            {
                try
                {
                    if (member.IndexOf('(') >= 0)
                        throw new BindingExpressionException(
                            $"Calling methods in '{expr}' is not allowed in binding expressions, use a filter instead.",
                            member.Value, expr.Value);

                    var indexerPos = member.IndexOf('[');
                    if (indexerPos >= 0)
                    {
                        var prop = member.LeftPart('[');
                        var indexer = member.RightPart('[');
                        indexer.ParseJsExpression(out var token);

                        if (token is JsCallExpression)
                            throw new BindingExpressionException($"Only constant binding expressions are supported: '{expr}'",
                                member.Value, expr.Value);

                        var value = JsToken.UnwrapValue(token);

                        var valueExpr = value == null
                            ? (Expression) Expression.Call(
                                typeof(TemplatePageUtils).GetStaticMethod(nameof(EvaluateBinding)),
                                scope,
                                Expression.Constant(token))
                            : Expression.Constant(value);

                        if (currType == typeof(string))
                        {
                            body = CreateStringIndexExpression(body, token, scope, valueExpr, ref currType);
                        }
                        else if (currType.IsArray)
                        {
                            if (token != null)
                            {
                                var evalAsInt = typeof(TemplatePageUtils).GetStaticMethod(nameof(EvaluateBindingAs))
                                    .MakeGenericMethod(typeof(int));
                                body = Expression.ArrayIndex(body,
                                    Expression.Call(evalAsInt, scope, Expression.Constant(token)));
                            }
                            else
                            {
                                body = Expression.ArrayIndex(body, valueExpr);
                            }
                        }
                        else if (depth == 0)
                        {
                            var pi = AssertProperty(currType, "Item", expr);
                            currType = pi.PropertyType;

                            if (token != null)
                            {
                                var indexType = pi.GetGetMethod()?.GetParameters().FirstOrDefault()?.ParameterType;
                                if (indexType != typeof(object))
                                {
                                    var evalAsInt = typeof(TemplatePageUtils).GetStaticMethod(nameof(EvaluateBindingAs))
                                        .MakeGenericMethod(indexType);
                                    valueExpr = Expression.Call(evalAsInt, scope, Expression.Constant(token));
                                }
                            }

                            body = Expression.Property(body, "Item", valueExpr);
                        }
                        else
                        {
                            var pi = AssertProperty(currType, prop.Value, expr);
                            currType = pi.PropertyType;
                            body = Expression.PropertyOrField(body, prop.Value);

                            if (currType == typeof(string))
                            {
                                body = CreateStringIndexExpression(body, token, scope, valueExpr, ref currType);
                            }
                            else
                            {
                                var indexMethod = currType.GetMethod("get_Item", new[] {value.GetType()});
                                body = Expression.Call(body, indexMethod, valueExpr);
                                currType = indexMethod.ReturnType;
                            }
                        }
                    }
                    else
                    {
                        if (depth >= 1)
                        {
                            var memberName = member.Value;
                            if (typeof(IDictionary).IsAssignableFrom(currType))
                            {
                                var pi = AssertProperty(currType, "Item", expr);
                                currType = pi.PropertyType;
                                body = Expression.Property(body, "Item", Expression.Constant(memberName));
                            }
                            else
                            {
                                body = Expression.PropertyOrField(body, memberName);
                                var pi = currType.GetProperty(memberName);
                                if (pi != null)
                                {
                                    currType = pi.PropertyType;
                                }
                                else
                                {
                                    var fi = currType.GetField(memberName);
                                    if (fi != null)
                                        currType = fi.FieldType;
                                }
                                
                            }
                        }
                    }

                    depth++;
                }
                catch (BindingExpressionException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new BindingExpressionException($"Could not compile '{member}' from expression '{expr}'", member.Value,
                        expr.Value, e);
                }
            }
            return body;
        }

        public static Action<TemplateScopeContext, object, object> CompileAssign(Type type, StringSegment expr)
        {
            var scope = Expression.Parameter(typeof(TemplateScopeContext), "scope");
            var instance = Expression.Parameter(typeof(object), "instance");
            var valueToAssign = Expression.Parameter(typeof(object), "valueToAssign");

            var body = CreateBindingExpression(type, expr, scope, instance);
            if (body is IndexExpression propItemExpr)
            {
                var mi = propItemExpr.Indexer.GetSetMethod();
                var indexExpr = propItemExpr.Arguments[0];
                body = Expression.Call(propItemExpr.Object, mi, indexExpr, valueToAssign);
            }
            else if (body is BinaryExpression binaryExpr && binaryExpr.NodeType == ExpressionType.ArrayIndex)
            {
                var arrayInstance = binaryExpr.Left;
                var indexExpr = binaryExpr.Right;
                
                body = Expression.Assign(
                    Expression.ArrayAccess(arrayInstance, indexExpr), 
                    valueToAssign);
            }
            else
            {
                throw new BindingExpressionException($"Assignment expression for '{expr}' not supported yet", "valueToAssign", expr.Value);
            }

            return Expression.Lambda<Action<TemplateScopeContext, object, object>>(body, scope, instance, valueToAssign).Compile();
        }

        private static Expression CreateStringIndexExpression(Expression body, JsToken binding, ParameterExpression scope,
            Expression valueExpr, ref Type currType)
        {
            body = Expression.Call(body, typeof(string).GetMethod("ToCharArray", Type.EmptyTypes));
            currType = typeof(char[]);

            if (binding != null)
            {
                var evalAsInt = typeof(TemplatePageUtils).GetStaticMethod(nameof(EvaluateBindingAs))
                    .MakeGenericMethod(typeof(int));
                body = Expression.ArrayIndex(body, Expression.Call(evalAsInt, scope, Expression.Constant(binding)));
            }
            else
            {
                body = Expression.ArrayIndex(body, valueExpr);
            }
            return body;
        }

        public static object EvaluateBinding(TemplateScopeContext scope, JsToken token)
        {
            var result = token.Evaluate(scope);
            return result;
        }

        public static T EvaluateBindingAs<T>(TemplateScopeContext scope, JsToken token)
        {
            var result = EvaluateBinding(scope, token);
            var converted = result.ConvertTo<T>();
            return converted;
        }

        private static PropertyInfo AssertProperty(Type currType, string prop, StringSegment expr)
        {
            var pi = currType.GetProperty(prop);
            if (pi == null)
                throw new ArgumentException(
                    $"Property '{prop}' does not exist on Type '{currType.Name}' from binding expression '{expr}'");
            return pi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWhiteSpace(this char c) =>
            c == ' ' || (c >= '\x0009' && c <= '\x000d') || c == '\x00a0' || c == '\x0085';
    }
}