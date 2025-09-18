namespace PhotoCli.Models.Enums;

public enum GroupByFolderType : byte
{
	Unset = 0,
	YearMonthDay = 1,
	YearMonth = 2,
	Year = 3,
	AddressFlat = 4,
	AddressHierarchy = 5,
	///<summary>{Decade}s/{Year}/{Year}{Month}{ShortMonthName}/{Year}-{Month}-{Day} {EventName}</summary>
	DecadeYearYearShortMonthNameYearMonthDay = 10,
}
