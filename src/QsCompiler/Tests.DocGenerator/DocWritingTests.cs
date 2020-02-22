﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using static Microsoft.Quantum.QsCompiler.Documentation.Testing.Utils;

namespace Microsoft.Quantum.QsCompiler.Documentation.Testing
{
    using ArgDeclType = LocalVariableDeclaration<QsLocalSymbol>;
    using QsType = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;

    public class DocWritingTests
    {
        /// <summary>
        /// Tests that internal and private items are skipped when generating documentation for a namespace.
        /// </summary>
        [Fact]
        public void ExcludeInaccessible()
        {
            var elements =
                new[] { AccessModifier.DefaultAccess, AccessModifier.Internal, AccessModifier.Private }
                .SelectMany(access =>
                {
                    var source = NonNullable<string>.New("Tests.qs");
                    var unit = ResolvedType.New(QsType.UnitType);

                    var signature = new ResolvedSignature(Array.Empty<QsLocalSymbol>().ToImmutableArray(),
                                                          unit,
                                                          unit,
                                                          CallableInformation.NoInformation);
                    var argumentTuple = QsTuple<ArgDeclType>.NewQsTuple(ImmutableArray.Create<QsTuple<ArgDeclType>>());
                    var callable = new QsCallable(kind: QsCallableKind.Operation,
                                                  fullName: MakeFullName(access + "Operation"),
                                                  attributes: ImmutableArray<QsDeclarationAttribute>.Empty,
                                                  modifiers: new Modifiers(access),
                                                  sourceFile: source,
                                                  location: ZeroLocation,
                                                  signature: signature,
                                                  argumentTuple: argumentTuple,
                                                  specializations: ImmutableArray.Create<QsSpecialization>(),
                                                  documentation: ImmutableArray.Create<string>(),
                                                  comments: QsComments.Empty);

                    var typeItems = QsTuple<QsTypeItem>.NewQsTuple(
                        ImmutableArray.Create(QsTuple<QsTypeItem>.NewQsTupleItem(QsTypeItem.NewAnonymous(unit))));
                    var type = new QsCustomType(fullName: MakeFullName(access + "Type"),
                                                attributes: ImmutableArray<QsDeclarationAttribute>.Empty,
                                                modifiers: new Modifiers(access),
                                                sourceFile: source,
                                                location: ZeroLocation,
                                                type: unit,
                                                typeItems: typeItems,
                                                documentation: ImmutableArray.Create<string>(),
                                                comments: QsComments.Empty);
                    return new[]
                    {
                        QsNamespaceElement.NewQsCallable(callable),
                        QsNamespaceElement.NewQsCustomType(type)
                    };
                });
            var emptyLookup = Array.Empty<ImmutableArray<string>>().ToLookup(x => NonNullable<string>.New(""));
            var ns = new QsNamespace(CanonName, elements.ToImmutableArray(), emptyLookup);
            var docNs = new DocNamespace(ns);
            var stream = new MemoryStream();
            docNs.WriteToStream(stream, null);

            var expected = @"### YamlMime:QSharpNamespace
uid: microsoft.quantum.canon
name: Microsoft.Quantum.Canon
operations:
- uid: microsoft.quantum.canon.defaultaccessoperation
  summary: ''
newtypes:
- uid: microsoft.quantum.canon.defaultaccesstype
  summary: ''
...
";
            var actual = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Equal(expected, actual);
        }
    }
}
