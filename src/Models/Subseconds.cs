using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Models;

public class SubSeconds
{
	public string Raw { get; }
	public int Digits { get; }
	public int Milliseconds { get; } // siempre entre 0-999

	public SubSeconds(string raw)
	{
		Raw = raw ?? "0";
		Digits = Raw.Length;

		if (!int.TryParse(Raw, out var val))
			val = 0;

		// Normalizamos a fracción de segundo según nº de dígitos
		double fraction = val / Math.Pow(10, Digits); // ej: "865655" → 0.865655 sec

		// Pasamos a ms y redondeamos
		Milliseconds = (int)Math.Round(fraction * 1000, MidpointRounding.AwayFromZero);

		// Controlamos overflow tipo "999.9 → 1000"
		if (Milliseconds == 1000)
			Milliseconds = 999;
	}

	public string Padded()
		=> Milliseconds.ToString("D3");

	public override string ToString()
		=> Padded();
}

