using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace OccupationAnnexation
{
    /// <summary>
    /// Moves items between a town's stockpile and a caravan (take / gift), or picks items for a delivery.
    /// </summary>
    public class Dialog_StockpileTransfer : Window
    {
        private enum Mode
        {
            TakeToCaravan,
            GiftFromCaravan,
            Delivery
        }

        private const float LoyaltyPerGiftValue = 1f / 60f;
        private const float MaxLoyaltyPerGift = 25f;
        private static readonly Vector2 ButtonSize = new Vector2(160f, 40f);

        private readonly OccupiedSettlement town;
        private readonly Caravan caravan;
        private readonly Map deliveryTarget;
        private readonly Mode mode;
        private bool byPods;
        private int etaCaravanTicks = -1;
        private int etaPodTicks = -1;

        private List<TransferableOneWay> transferables;
        private TransferableOneWayWidget widget;

        public override Vector2 InitialSize => new Vector2(1024f, UI.screenHeight - 100f);

        protected override float Margin => 17f;

        public Dialog_StockpileTransfer(OccupiedSettlement town, Caravan caravan, bool gift)
        {
            this.town = town;
            this.caravan = caravan;
            mode = gift ? Mode.GiftFromCaravan : Mode.TakeToCaravan;
            Init();
        }

        public Dialog_StockpileTransfer(OccupiedSettlement town, Map deliveryTarget)
        {
            this.town = town;
            this.deliveryTarget = deliveryTarget;
            mode = Mode.Delivery;
            byPods = DeliveryUtility.CanUsePods(town);
            Init();
        }

        private void Init()
        {
            forcePause = true;
            absorbInputAroundWindow = true;
            doCloseX = true;
        }

        public override void PostOpen()
        {
            base.PostOpen();
            Recache();
        }

        private IEnumerable<Thing> SourceThings()
        {
            if (mode == Mode.GiftFromCaravan)
            {
                return CaravanInventoryUtility.AllInventoryItems(caravan);
            }
            return town.Stock;
        }

        private void Recache()
        {
            transferables = new List<TransferableOneWay>();
            foreach (Thing thing in SourceThings())
            {
                TransferableOneWay transferable = TransferableUtility.TransferableMatching(thing, transferables, TransferAsOneMode.PodsOrCaravanPacking);
                if (transferable == null)
                {
                    transferable = new TransferableOneWay();
                    transferables.Add(transferable);
                }
                if (!transferable.things.Contains(thing))
                {
                    transferable.things.Add(thing);
                }
            }

            switch (mode)
            {
                case Mode.TakeToCaravan:
                    widget = new TransferableOneWayWidget(transferables, town.Label, caravan.Name, "OA_StockCountTip".Translate(),
                        drawMass: true, availableMassGetter: () => caravan.MassCapacity - caravan.MassUsage, tile: town.Tile, drawMarketValue: true);
                    break;
                case Mode.GiftFromCaravan:
                    widget = new TransferableOneWayWidget(transferables, caravan.Name, town.Label, "OA_CaravanCountTip".Translate(),
                        drawMass: false, tile: town.Tile, drawMarketValue: true);
                    break;
                default:
                    widget = new TransferableOneWayWidget(transferables, town.Label, deliveryTarget.Parent.Label, "OA_StockCountTip".Translate(),
                        drawMass: true, tile: town.Tile, drawMarketValue: true);
                    break;
            }
        }

        private float SelectedValue()
        {
            float value = 0f;
            foreach (TransferableOneWay transferable in transferables)
            {
                if (transferable.CountToTransfer > 0 && transferable.AnyThing != null)
                {
                    value += transferable.AnyThing.MarketValue * transferable.CountToTransfer;
                }
            }
            return value;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect titleRect = new Rect(0f, 0f, inRect.width, 35f);
            Text.Font = GameFont.Medium;
            Widgets.Label(titleRect, Title());
            Text.Font = GameFont.Small;

            float infoHeight = 0f;
            if (mode != Mode.TakeToCaravan)
            {
                infoHeight = 30f;
                Rect infoRect = new Rect(0f, titleRect.yMax, inRect.width, infoHeight);
                DrawInfo(infoRect);
            }

            Rect widgetRect = new Rect(0f, titleRect.yMax + infoHeight + 4f, inRect.width, inRect.height - titleRect.height - infoHeight - ButtonSize.y - 14f);
            widget.OnGUI(widgetRect);

            Rect buttonsRect = new Rect(0f, inRect.height - ButtonSize.y, inRect.width, ButtonSize.y);
            DrawButtons(buttonsRect);
        }

        private string Title()
        {
            switch (mode)
            {
                case Mode.TakeToCaravan:
                    return "OA_DialogTakeTitle".Translate(town.Label);
                case Mode.GiftFromCaravan:
                    return "OA_DialogGiftTitle".Translate(town.Label);
                default:
                    return "OA_DialogDeliveryTitle".Translate(town.Label, deliveryTarget.Parent.Label);
            }
        }

        private void DrawInfo(Rect rect)
        {
            if (mode == Mode.GiftFromCaravan)
            {
                float gain = Mathf.Min(MaxLoyaltyPerGift, SelectedValue() * LoyaltyPerGiftValue);
                Widgets.Label(rect, "OA_GiftLoyaltyPreview".Translate(gain.ToString("F1")));
                return;
            }
            float x = 0f;
            if (DeliveryUtility.CanUsePods(town))
            {
                Rect checkRect = new Rect(0f, rect.y, 320f, 24f);
                Widgets.CheckboxLabeled(checkRect, "OA_DeliveryByPods".Translate(), ref byPods);
                x = checkRect.xMax + 20f;
            }
            if (etaCaravanTicks < 0)
            {
                etaCaravanTicks = DeliveryUtility.TravelTicks(town, deliveryTarget, byPods: false);
                etaPodTicks = DeliveryUtility.TravelTicks(town, deliveryTarget, byPods: true);
            }
            int ticks = byPods ? etaPodTicks : etaCaravanTicks;
            Widgets.Label(new Rect(x, rect.y, rect.width - x, 24f), "OA_DeliveryEta".Translate(ticks.ToStringTicksToPeriod()));
        }

        private void DrawButtons(Rect rect)
        {
            Rect acceptRect = new Rect(rect.width - ButtonSize.x, rect.y, ButtonSize.x, ButtonSize.y);
            if (Widgets.ButtonText(acceptRect, AcceptLabel()))
            {
                if (TryAccept())
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    Close(doCloseSound: false);
                }
            }
            if (Widgets.ButtonText(new Rect(rect.width / 2f - ButtonSize.x / 2f, rect.y, ButtonSize.x, ButtonSize.y), "ResetButton".Translate()))
            {
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                Recache();
            }
            if (Widgets.ButtonText(new Rect(0f, rect.y, ButtonSize.x, ButtonSize.y), "CancelButton".Translate()))
            {
                Close();
            }
        }

        private string AcceptLabel()
        {
            switch (mode)
            {
                case Mode.TakeToCaravan:
                    return "OA_ButtonTake".Translate();
                case Mode.GiftFromCaravan:
                    return "OA_ButtonGift".Translate();
                default:
                    return "OA_ButtonSend".Translate();
            }
        }

        private List<Thing> TakeSelected()
        {
            var taken = new List<Thing>();
            foreach (TransferableOneWay transferable in transferables)
            {
                int remaining = transferable.CountToTransfer;
                foreach (Thing thing in transferable.things.ToList())
                {
                    if (remaining <= 0)
                    {
                        break;
                    }
                    if (thing.stackCount <= remaining)
                    {
                        remaining -= thing.stackCount;
                        thing.holdingOwner?.Remove(thing);
                        taken.Add(thing);
                    }
                    else
                    {
                        taken.Add(thing.SplitOff(remaining));
                        remaining = 0;
                    }
                }
            }
            return taken;
        }

        private bool TryAccept()
        {
            float giftValue = SelectedValue();
            List<Thing> selected = TakeSelected();
            if (selected.Count == 0)
            {
                Messages.Message("OA_NothingSelected".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            switch (mode)
            {
                case Mode.TakeToCaravan:
                    foreach (Thing thing in selected)
                    {
                        caravan.AddPawnOrItem(thing, addCarriedPawnToWorldPawnsIfAny: true);
                    }
                    break;
                case Mode.GiftFromCaravan:
                    foreach (Thing thing in selected)
                    {
                        town.Store(thing);
                    }
                    float gain = Mathf.Min(MaxLoyaltyPerGift, giftValue * LoyaltyPerGiftValue);
                    town.loyalty = Mathf.Min(100f, town.loyalty + gain);
                    Messages.Message("OA_MessageGiftAccepted".Translate(town.Label, gain.ToString("F1")), town, MessageTypeDefOf.PositiveEvent);
                    break;
                default:
                    DeliveryUtility.RequestDelivery(town, deliveryTarget, selected, byPods && DeliveryUtility.CanUsePods(town));
                    break;
            }
            return true;
        }
    }
}
