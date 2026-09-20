using LovelyCarDataCapture.Profile;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void AtsrLmuCanonicalDevelopmentPath()
        {
            Equal(@"C:\SimHub\_ATSR_DevelopmentData\rpm_data\lamborghini-sc63.json",
                  AtsrCompatibility.DevelopmentFilePath(@"C:\SimHub", "LMU", "Lamborghini Iron Lynx 2024"),
                  "LMU raw id uses ATSR's SC63 filename");
            Equal(@"C:\SimHub\_ATSR_DevelopmentData\rpm_data\lamborghini-sc63.json",
                  AtsrCompatibility.DevelopmentFilePath(@"C:\SimHub", "Le Mans Ultimate", "Lamborghini Iron Lynx 2024"),
                  "full LMU game name uses the same filename");
            Equal(@"C:\SimHub\_ATSR_DevelopmentData\rpm_data\lamborghini-iron-lynx-2024x.json",
                  AtsrCompatibility.DevelopmentFilePath(@"C:\SimHub", "LMU", "Lamborghini Iron Lynx 2024x"),
                  "nearby car ids are not aliased");
            Equal(@"C:\SimHub\_ATSR_DevelopmentData\rpm_data\lamborghini-iron-lynx-2024.json",
                  AtsrCompatibility.DevelopmentFilePath(@"C:\SimHub", "Automobilista2", "Lamborghini Iron Lynx 2024"),
                  "the alias is scoped to LMU");
        }
    }
}
