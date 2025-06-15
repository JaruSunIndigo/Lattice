using System;
using System.Reflection;
using System.Reflection.Emit;
using GrEmit;

namespace Lattice.IR.Nodes
{
    /// <summary>
    ///     Node that returns a constant value string. Value must always be a valid c# identifier because it gets embedded
    ///     in the function name.
    /// </summary>
    public class LiteralStringIRNode : FunctionIRNode
    {
        public string Literal;

        public LiteralStringIRNode(string literal)
        {
            Literal = literal;

            CheckExceptions = false;
            DebugName = "literal " + literal;
            DefaultReturnType = typeof(string);
        }

        public static void CodeGen(GraphCompilation compilation)
        {
            compilation.DeduplicatedCodeGen<string, LiteralStringIRNode>("String_Literals", node => node.Literal,
                (node, builder) =>
                {
                    MethodBuilder method = builder.DefineMethod($"{node.Literal}",
                        MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);

                    GroboIL emit = new(method);
                    emit.Ldstr(node.Literal);
                    emit.Ret();

                    return method;
                });
        }
    }
}
