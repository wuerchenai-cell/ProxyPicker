using ProxyPicker.Domain;

namespace ProxyPicker.Tests
{
    public sealed class BootstrapSmokeTests
    {
        [TestFact]
        public void ProductInfo_ShouldExposeV3Metadata()
        {
            AssertEx.Equal("ProxyPicker", ProductInfo.Name);
            AssertEx.Equal("3.0.0", ProductInfo.Version);
        }
    }
}
