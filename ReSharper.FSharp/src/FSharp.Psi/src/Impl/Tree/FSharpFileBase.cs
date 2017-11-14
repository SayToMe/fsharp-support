﻿using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Application.Threading;
using JetBrains.ReSharper.Plugins.FSharp.Common.Checker;
using JetBrains.ReSharper.Plugins.FSharp.Psi.Tree;
using JetBrains.ReSharper.Plugins.FSharp.Psi.Util;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Tree;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.Text;
using JetBrains.Util;
using Microsoft.FSharp.Compiler.SourceCodeServices;
using Microsoft.FSharp.Core;

namespace JetBrains.ReSharper.Plugins.FSharp.Psi.Impl.Tree
{
  internal abstract class FSharpFileBase : FileElementBase, IFSharpFileCheckInfoOwner
  {
    private static readonly object ourGetSymbolsLock = new object();

    private Dictionary<int, FSharpResolvedSymbolUse> myDeclarationSymbols;
    private Dictionary<int, FSharpResolvedSymbolUse> myResolvedSymbols;
    public FSharpCheckerService CheckerService { get; set; }
    public TokenBuffer ActualTokenBuffer { get; set; }
    public FSharpOption<FSharpParseFileResults> ParseResults { get; set; }
    public override PsiLanguageType Language => FSharpLanguage.Instance;

    public FSharpOption<FSharpParseAndCheckResults> GetParseAndCheckResults(bool allowStaleResults,
      [CanBeNull] Action interruptChecker = null)
    {
      var sourceFile = GetSourceFile();
      Assertion.AssertNotNull(sourceFile, "sourceFile != null");
      return CheckerService.ParseAndCheckFile(sourceFile, allowStaleResults);
    }

    private void UpdateSymbols()
    {
      lock (ourGetSymbolsLock)
        if (myDeclarationSymbols == null)
        {
          var interruptChecker = new SeldomInterruptCheckerWithCheckTime(100);
          var checkResults = GetParseAndCheckResults(false);
          var document = GetSourceFile()?.Document;
          var buffer = document?.Buffer;

          var symbolUses = checkResults?.Value.CheckResults.GetAllUsesOfAllSymbolsInFile().RunAsTask();
          if (symbolUses == null || document == null)
            return;

          // add separate APIs to FCS to get resoved symbols and bindings?
          myResolvedSymbols = new Dictionary<int, FSharpResolvedSymbolUse>(symbolUses.Length);
          myDeclarationSymbols = new Dictionary<int, FSharpResolvedSymbolUse>(symbolUses.Length / 4);

          foreach (var symbolUse in symbolUses)
          {
            var symbol = symbolUse.Symbol;
            var range = symbolUse.RangeAlternate;

            var startOffset = document.GetOffset(range.Start);
            var endOffset = document.GetOffset(range.End);
            var mfv = symbol as FSharpMemberOrFunctionOrValue;

            if (symbolUse.IsFromDefinition)
            {
              if (mfv != null)
              {
                // workaround for auto-properties, see visualfsharp#3939
                var mfvLogicalName = mfv.LogicalName;
                if (mfvLogicalName.EndsWith("@", StringComparison.Ordinal))
                  continue;

                // visualfsharp#3939
                if (mfvLogicalName.Equals("v", StringComparison.Ordinal) &&
                    myDeclarationSymbols.ContainsKey(startOffset))
                  continue;

                // visualfsharp#3943, visualfsharp#3933
                if (!mfvLogicalName.Equals(".ctor", StringComparison.Ordinal) &&
                    !(FindTokenAt(new TreeOffset(endOffset - 1)) is FSharpIdentifierToken))
                  continue;
              }
              else
              {
                // workaround for compiler generated symbols (e.g. fields auto-properties)
                if (!(FindTokenAt(new TreeOffset(endOffset - 1)) is FSharpIdentifierToken))
                  continue;
              }

              var textRange = new TextRange(startOffset, endOffset);
              myDeclarationSymbols[startOffset] = new FSharpResolvedSymbolUse(symbolUse, textRange);
              myResolvedSymbols.Remove(startOffset);
            }
            else
            {
              // workaround for indexer properties, visualfsharp#3933
              if (startOffset == endOffset ||
                  mfv != null && mfv.IsProperty && buffer[endOffset - 1] == ']')
                continue;

              var nameRange = FixRange(new TextRange(startOffset, endOffset), buffer);

              // workaround for implicit type usages (e.g. in members with optional params), visualfsharp#3933
              if (symbol is FSharpEntity &&
                  !(FindTokenAt(new TreeOffset(nameRange.EndOffset - 1)) is FSharpIdentifierToken))
                continue;

              if (!myDeclarationSymbols.ContainsKey(startOffset))
                myResolvedSymbols[nameRange.StartOffset] = new FSharpResolvedSymbolUse(symbolUse, nameRange);
            }
            interruptChecker.CheckForInterrupt();
          }
        }
    }

    private TextRange FixRange(TextRange range, IBuffer buffer)
    {
      // todo: remove when visualfsharp#3920 is fixed
      var endOffset = range.EndOffset;
      var startOffset = range.StartOffset;

      // trim foo.``bar`` to ``bar``
      if (buffer.Length > 4 && endOffset > 4 &&
          buffer[endOffset - 1] == '`' && buffer[endOffset - 2] == '`')
        for (var i = endOffset - 4; i > startOffset; i--)
          if (buffer[i] == '`' && buffer[i + 1] == '`')
            return new TextRange(i, endOffset);

      // trim foo.bar to bar
      for (var i = endOffset - 1; i > startOffset; i--)
        if (buffer[i].Equals('.'))
          return new TextRange(i + 1, endOffset);
      return range;
    }

    public FSharpResolvedSymbolUse[] GetAllResolvedSymbols()
    {
      lock (ourGetSymbolsLock)
      {
        if (myDeclarationSymbols == null)
          UpdateSymbols();
        return myResolvedSymbols.Values.AsArray();
      }
    }

    public FSharpResolvedSymbolUse[] GetAllDeclaredSymbols()
    {
      lock (ourGetSymbolsLock)
      {
        if (myDeclarationSymbols == null)
          UpdateSymbols();
        return myDeclarationSymbols.Values.AsArray();
      }
    }

    public FSharpSymbol GetSymbolUse(int offset)
    {
      lock (ourGetSymbolsLock)
      {
        if (myDeclarationSymbols == null)
          UpdateSymbols();
        var resolvedSymbol = myResolvedSymbols.TryGetValue(offset);
        if (resolvedSymbol == null)
          return null;

        return myDeclarationSymbols.TryGetValue(offset) == null
          ? resolvedSymbol.SymbolUse.Symbol
          : null;
      }
    }

    public FSharpSymbol GetSymbolDeclaration(int offset)
    {
      lock (ourGetSymbolsLock)
      {
        if (myDeclarationSymbols == null)
          UpdateSymbols();
        return myDeclarationSymbols?.TryGetValue(offset)?.SymbolUse.Symbol;
      }
    }

    public virtual void Accept(TreeNodeVisitor visitor) => visitor.VisitNode(this);

    public virtual void Accept<TContext>(TreeNodeVisitor<TContext> visitor, TContext context) =>
      visitor.VisitNode(this, context);

    public virtual TReturn Accept<TContext, TReturn>(TreeNodeVisitor<TContext, TReturn> visitor, TContext context) =>
      visitor.VisitNode(this, context);
  }
}