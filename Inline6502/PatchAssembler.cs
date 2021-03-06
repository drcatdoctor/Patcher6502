﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetAsm;

namespace Patcher6502
{
	public class AssemblyError : Exception
	{
		public AssemblyError(string message) : base(message)
		{
		}
	}

    /// <summary>
    /// Provides methods to generate assembled 6502 code.
    /// 
    /// You may want to set bool fields <code>ShowListingInConsole</code> and/or 
    /// <code>ShowWarningsInConsole</code> to true during debug and testing.
    /// </summary>
	public class PatchAssembler
	{
        public bool ShowListingInConsole = false;
        public bool ShowWarningsInConsole = true;

        public PatchAssembler() { }

		private class PatchController : AssemblyController
		{
			private string _raw_input;
			private string _reference_name;
            private PatchAssembler _container;

			public PatchController(PatchAssembler container, AssemblyOptions options) : base(options)
			{
                _container = container;
			}

			public byte[] Assemble(string name, string asm)
			{
				_reference_name = name;
				_raw_input = asm;

                var source = Preprocess();
				if (Log.HasErrors)
					return null;

                FirstPass(source);
				if (Log.HasErrors)
					return null;

                SecondPass();

				if (Log.HasErrors)
					return null;


				if (_container.ShowListingInConsole)
                {
                    Console.WriteLine($"-- Listing for {name}:");
                    Console.WriteLine(GetListing());
                    Console.WriteLine($"-- Listing complete for {name}:");
                }

                return Output.GetCompilation().ToArray();
			}

			private IEnumerable<SourceLine> GetSourceLines()
			{
				int currentline = 1;
				IList<SourceLine> sourcelines = new List<SourceLine>();
				foreach (string unprocessedline in _raw_input.Replace("\r","").Split('\n'))
				{
					try
					{
						var line = new SourceLine(_reference_name, currentline, unprocessedline);
						line.Parse(
							token => Controller.IsInstruction(token) ||
							         Reserved.IsReserved(token) ||
							         (token.StartsWith(".") && Macro.IsValidMacroName(token.Substring(1))) ||
							         token == "="
						);
						sourcelines.Add(line);
					}
					catch (Exception ex)
					{
						Controller.Log.LogEntry(_reference_name, currentline, ex.Message);
					}
					currentline++;
				}

				sourcelines = _preprocessor.Preprocess(sourcelines).ToList();
				return sourcelines;
			}

			protected override IEnumerable<SourceLine> Preprocess()
			{
				var source = new List<SourceLine>();

				source.AddRange(ProcessDefinedLabels());
				source.AddRange(GetSourceLines());

				if (Log.HasErrors)
					return null;

				// weird hack in AssemblyController, copying here to be consistent
				source.ForEach(line =>
					line.Operand = Regex.Replace(line.Operand, @"\s?\*\s?", "*"));

				return source;
			}

		}

		/// <summary>
		/// Assembles the 6502 asm provided into raw bytes.
		/// </summary>
		/// <param name="origin">The address this code will run at. e.g. 0x8000. Must be between 0 and 0xFFFF.</param>
		/// <param name="name">A human-readable reference, e.g. a subroutine name.</param>
		/// <param name="asm">the 6502 asm source, including newline characters.</param>
		/// <example>
		/// var newBytes = InlineAssembler.assemble(0x8000, "FixTheThing", @"
		///   LDA $30
		///   JMP $8044
		///  ");
		/// </example>
		/// <exception cref="AssemblyError">if something goes wrong.</exception>
		public byte[] Assemble(int origin, string name, string asm, Dictionary<string,int> variables = null, Dictionary<string, int> labels = null)
		{
			if (origin < 0 || origin > 0xFFFF)
				throw new ArgumentOutOfRangeException(nameof(origin));

            // it is very important for caseSensitive to be false here. it interprets even instructions as case-sensitive with it on.
            var options = new AssemblyOptions();

            var controller = new PatchController(this, options);
            controller.AddAssembler(new Asm6502(controller));

            // have to do this every pass because each one clears everything out every time...
            controller.OnBeginningOfPass += delegate (object sender, EventArgs e)
            {
                // load up the symbol table
                variables?.ToList().ForEach(pair => controller.Symbols.Variables.SetSymbol(pair.Key, pair.Value, isStrict: false));
                labels?.ToList().ForEach(pair => controller.Symbols.Labels.SetSymbol(pair.Key, pair.Value, isStrict: false));

                // set PC
                controller.Output.SetPC(origin);
            };

            var result = controller.Assemble(name, asm);

			if (ShowWarningsInConsole)
			    controller.Log.DumpAll();
			else if (controller.Log.HasErrors)
				controller.Log.DumpErrors();

			if (result is null)
				throw new AssemblyError($"Unable to assemble {name} at {origin}; see console output for details");

			return result;
		}

        /// <summary>
        /// Does the same thing as assemble(), but asserts (if DEBUG) that the resulting size is what you expect.
        /// Intended to catch accidental patch-too-big type errors.
        /// </summary>
        /// <remarks>To be clear: the assert will not check anything in Release builds. This is a designed feature of Debug.Assert.</remarks>
        /// <param name="size">Asserted size of the resulting assembled code in bytes.</param>
        /// <seealso cref="Assemble(int, string, string)"/>
        public byte[] AssembleAndAssertSize(int origin, int size, string name, string asm)
		{
			var result = Assemble(origin, name, asm);
			Debug.Assert(result.Length == size);
			return result;
		}

    }
}
