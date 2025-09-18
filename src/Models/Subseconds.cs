using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Models;

public class SubSeconds
{
	public string Raw { get; }
	public int Value { get; }
	public int Digits { get; }

	public SubSeconds(string raw)
	{
		Raw = raw;
		Digits = raw.Length;
		Value = int.TryParse(raw, out var val) ? val : 0;
	}

	public string Padded(int totalDigits = 3)
		=> Value.ToString($"D{totalDigits}");

	public double AsMilliseconds()
		=> Digits switch
		{
			1 => Value * 100,   // "1" → 100ms
			2 => Value * 10,    // "12" → 120ms
			3 => Value,         // "123" → 123ms
			_ => 0
		};

	public override string ToString() => Raw;
}
