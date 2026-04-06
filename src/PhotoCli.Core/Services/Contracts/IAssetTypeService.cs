using PhotoCli.Core.Models.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Contracts
{
	public interface IAssetTypeService
	{
		AssetType GetAssetType(string extension);
	}
}
