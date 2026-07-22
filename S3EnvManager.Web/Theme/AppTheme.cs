using MudBlazor;

namespace S3EnvManager.Web.Theme;

// 전체 화면에 일괄 적용되는 사내 운영 도구용 MudBlazor 테마.
public static class AppTheme
{
	public static readonly MudTheme Default = new()
	{
		PaletteLight = new PaletteLight
		{
			Primary = "#3B5BDB",
			Secondary = "#57606A",
			AppbarBackground = "#FFFFFF",
			AppbarText = "#1F2328",
			Background = "#F6F7F9",
			BackgroundGray = "#F1F3F5",
			Surface = "#FFFFFF",
			DrawerBackground = "#FFFFFF",
			DrawerText = "#1F2328",
			DrawerIcon = "#57606A",
			TextPrimary = "#1F2328",
			TextSecondary = "#57606A",
			Divider = "#E4E7EB",
			DividerLight = "#EDEFF2",
			LinesDefault = "#E4E7EB",
			LinesInputs = "#D0D5DD",
			TableLines = "#EAECEF",
			TableHover = "#F1F3F5",
			Success = "#2E7D32",
			Warning = "#B54708",
			Error = "#C62828",
			Info = "#0B6BCB",
		},
		Typography = new Typography
		{
			Default = new DefaultTypography
			{
				FontFamily =
				[
					"-apple-system", "Segoe UI", "Malgun Gothic", "Apple SD Gothic Neo",
					"Noto Sans KR", "Roboto", "sans-serif",
				],
				FontSize = "0.875rem",
			},
			H3 = new H3Typography { FontSize = "1.5rem", FontWeight = "600" },
			H4 = new H4Typography { FontSize = "1.375rem", FontWeight = "600" },
			H5 = new H5Typography { FontSize = "1.25rem", FontWeight = "600" },
			H6 = new H6Typography { FontSize = "1rem", FontWeight = "600" },
		},
		LayoutProperties = new LayoutProperties
		{
			DefaultBorderRadius = "8px",
		},
	};
}