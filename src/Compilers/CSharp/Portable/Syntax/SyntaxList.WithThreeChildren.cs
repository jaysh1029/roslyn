﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    internal partial class SyntaxList
    {
        internal class WithThreeChildren : SyntaxList
        {
            private SyntaxNode child0;
            private SyntaxNode child1;
            private SyntaxNode child2;

            internal WithThreeChildren(Syntax.InternalSyntax.SyntaxList green, SyntaxNode parent, int position)
                : base(green, parent, position)
            {
            }

            internal override SyntaxNode GetNodeSlot(int index)
            {
                switch (index)
                {
                    case 0:
                        return this.GetRedElement(ref this.child0, 0);
                    case 1:
                        return this.GetRedElementIfNotToken(ref this.child1);
                    case 2:
                        return this.GetRedElement(ref this.child2, 2);
                    default:
                        return null;
                }
            }

            internal override SyntaxNode GetCachedSlot(int index)
            {
                switch (index)
                {
                    case 0:
                        return this.child0;
                    case 1:
                        return this.child1;
                    case 2:
                        return this.child2;
                    default:
                        return null;
                }
            }

            public override TResult Accept<TResult>(CSharpSyntaxVisitor<TResult> visitor)
            {
                throw new NotImplementedException();
            }

            public override void Accept(CSharpSyntaxVisitor visitor)
            {
                throw new NotImplementedException();
            }
        }
    }
}
