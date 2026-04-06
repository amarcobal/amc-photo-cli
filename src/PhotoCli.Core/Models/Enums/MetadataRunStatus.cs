using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Models.Enums
{
	public enum MetadataRunStatus
	{
		/// <summary>
		/// Estado inicial o no aplicable.
		/// </summary>
		NotApplicable = 0,

		/// <summary>
		/// Pasó todas las validaciones y el archivo será escrito o actualizado.
		/// </summary>
		ReadyToWrite = 1,

		/// <summary>
		/// Pasó todas las validaciones, pero no hay cambios que escribir (valor ya existe y no se sobrescribe, o el valor es idéntico).
		/// </summary>
		Kept = 2,

		/// <summary>
		/// Se salta la operación porque el archivo no tiene la identidad de media requerida (e.g., fecha de toma).
		/// </summary>
		SkippedIdentity = 3,

		/// <summary>
		/// Se salta la operación porque la validación de la plantilla falló (e.g., falta un tag 'Required').
		/// </summary>
		FailedTemplate = 4,

		/// <summary>
		/// La operación de escritura en disco falló (solo ocurre en modo Execute, no Dry Run).
		/// </summary>
		FailedWrite = 5
	}
}
