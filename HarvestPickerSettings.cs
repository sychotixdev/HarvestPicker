using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;

namespace HarvestPicker;

public class HarvestPickerSettings : ISettings
{
    //Mandatory setting to allow enabling/disabling your plugin
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public ListNode League { get; set; } = new ListNode() { Value = "Necropolis" };
    public RangeNode<int> PriceRefreshPeriodMinutes { get; set; } = new RangeNode<int>(15, 5, 60);

    [JsonIgnore]
    public ButtonNode ReloadPrices { get; set; } = new ButtonNode();
    public ToggleNode DrawRotationOnMap { get; set; } = new ToggleNode(true);

    public ColorNode BadColor { get; set; } = new ColorNode(Color.Red);
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.Yellow);
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.Green);

    public RangeNode<int> SeedsPerT1Plant { get; set; } = new RangeNode<int>(7, 0, 300);
    public RangeNode<int> SeedsPerT2Plant { get; set; } = new RangeNode<int>(18, 0, 300);
    public RangeNode<int> SeedsPerT3Plant { get; set; } = new RangeNode<int>(47, 0, 300);
    public RangeNode<int> SeedsPerT4Plant { get; set; } = new RangeNode<int>(235, 0, 900);
    public RangeNode<float> T4PlantWhiteSeedChance { get; set; } = new RangeNode<float>(0.12f, 0, 1f);

    public RangeNode<float> CropRotationT1UpgradeChance { get; set; } = new RangeNode<float>(0.25f, 0, 1f);
    public RangeNode<float> CropRotationT2UpgradeChance { get; set; } = new RangeNode<float>(0.204f, 0, 1f);
    public RangeNode<float> CropRotationT3UpgradeChance { get; set; } = new RangeNode<float>(0.035f, 0, 1f);
    public RangeNode<int> MaxPermutations { get; set; } = new RangeNode<int>(50000, 0, 3628800);
    public ToggleNode LogDetailedForCropRotation { get; set; } = new ToggleNode(false);


}
