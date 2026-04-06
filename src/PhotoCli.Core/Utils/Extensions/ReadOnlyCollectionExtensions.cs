using PhotoCli.Core.Models;

namespace PhotoCli.Core.Utils.Extensions;

public static class ReadOnlyCollectionExtensions
{
	public static void ThrowIfNotOrderedByPhotoTakenDate(this IReadOnlyCollection<Photo> list)
	{
		var orderedPhotosThatHavePhotoTakenDate = list.Where(w => w.HasOriginalDateTime).ToList();

		var validateSorted = orderedPhotosThatHavePhotoTakenDate
			.Zip(orderedPhotosThatHavePhotoTakenDate.Skip(1), (curr, next) => curr.OriginalDateTime <= next.OriginalDateTime)
			.All(x => x);

		if (!validateSorted)
			throw new PhotoCliException($"{nameof(list)} is not sorted by PhotoTakenDate");
	}
}
