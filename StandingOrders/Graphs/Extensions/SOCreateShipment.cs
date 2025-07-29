using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.CS;
using PX.Objects.IN;
using PX.Objects.SO;
using StandingOrders;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static StandingOrders.SOFilterExt;

namespace StandingOrders
{
    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public class SOCreateShipment_Extension : PXGraphExtension<SOCreateShipment>
    {
        /* ───────────────────────────────────────────────────────── */
        /* 1 – Inject Book-Series filter into the main SO query      */
        /* ───────────────────────────────────────────────────────── */
        public delegate void AddCommonFiltersDelegate(
            SOOrderFilter filter, PXSelectBase<SOOrder> cmd);

        [PXOverride]
        public void AddCommonFilters(
            SOOrderFilter filter,
            PXSelectBase<SOOrder> cmd,
            AddCommonFiltersDelegate baseMethod)
        {
            baseMethod(filter, cmd);

            var fExt = filter.GetExtension<SOFilterExt>();
            if (fExt?.UsrBookSeriesCD != null)
            {
                cmd.WhereAnd<
                    Where<SOOrderExt.usrBookSeriesCD,
                          Equal<Current<SOFilterExt.usrBookSeriesCD>>>>();

                PXTrace.WriteInformation(
                    $"AddCommonFilters – limited SOOrder view to BookSeriesCD '{fExt.UsrBookSeriesCD}'");
            }
        }

        /* ───────────────────────────────────────────────────────── */
        /* 2 – Remove stock Process buttons so ours is the only one */
        /* ───────────────────────────────────────────────────────── */
        public override void Initialize()
        {
            Base.Orders.SetProcessWorkflowAction(null, null, null, null);
            PXTrace.WriteInformation("Initialize – stock Process buttons disabled");
        }

        /* ───────────────────────────────────────────────────────── */
        /* 3 – Refresh orders when Book-Series filter changes        */
        /* ───────────────────────────────────────────────────────── */
        protected void _(
            Events.FieldUpdated<SOOrderFilter, SOFilterExt.usrBookSeriesCD> e)
        {
            Base.Orders.Cache.ClearQueryCache();
            Base.Orders.View.RequestRefresh();
            PXTrace.WriteInformation($"Filter updated – BookSeriesCD is now '{e.NewValue}'");
        }
        protected void _(
            Events.FieldUpdated<SOOrderFilter, SOOrderFilter.action> e)
        {
            var isShipAction = e.NewValue?.ToString() == SOCreateShipment.WellKnownActions.SOOrderScreen.CreateChildOrders;
            if (!isShipAction)
            {
                e.Cache.SetValueExt<SOOrderFilter.action>(e.Row, null);
            }
        }

        /* ───────────────────────────────────────────────────────── */
        /* 4 – Helper: compute next cycle + lead-time delta          */
        /* ───────────────────────────────────────────────────────── */
        private (DateTime? nextDate, TimeSpan delta) GetNextCycle(
            SeriesDetail det, Series series)
        {
            if (series?.CycleID == null ||
                det.CycleMajor == null ||
                det.CycleMinor == null)
                return (null, TimeSpan.Zero);

            DateTime today = Base.Accessinfo.BusinessDate.Value;

            CycleDetail next = PXSelect<
                                   CycleDetail,
                                   Where<CycleDetail.cycleID, Equal<Required<CycleDetail.cycleID>>,
                                     And<CycleDetail.cycleMajor, Equal<Required<CycleDetail.cycleMajor>>,
                                     And<CycleDetail.cycleMinor, Equal<Required<CycleDetail.cycleMinor>>,
                                     And<CycleDetail.date, Greater<Required<CycleDetail.date>>>>>>,
                                   OrderBy<Asc<CycleDetail.date>>>.
                               SelectWindowed(Base, 1, 1,            // first future row
                                              series.CycleID,
                                              det.CycleMajor,
                                              det.CycleMinor,
                                              today);

            if (next == null)
                return (null, TimeSpan.Zero);

            TimeSpan delta = next.Date.Value - today;
            return (next.Date, delta);
        }

        /* ───────────────────────────────────────────────────────── */
        /* 5 – Toolbar button: “Process Series”                      */
        /* ───────────────────────────────────────────────────────── */
        public PXAction<SOOrderFilter> ProcessSeries;
        [PXButton(CommitChanges = true)]
        [PXUIField(DisplayName = "Process Series")]
        protected IEnumerable processSeries(PXAdapter adapter)
        {
            var filter = Base.Filter.Current;
            var fExt = filter?.GetExtension<SOFilterExt>();

            if (fExt?.UsrBookSeriesCD == null)
                throw new PXException("Select a Book Series first.");

            PXTrace.WriteInformation($"ProcessSeries – start (BookSeriesCD = '{fExt.UsrBookSeriesCD}')");

            /* 5.1 – Collect InventoryIDs that will ship in this run  */
            DateTime cutOff = filter.EndDate
                           ?? filter.StartDate
                           ?? Base.Accessinfo.BusinessDate.Value;

            var shipItems = new HashSet<int>();
            foreach (SOOrder ord in Base.Orders.Select())
            {
                foreach (SOLine ln in PXSelect<
                                       SOLine,
                                       Where<SOLine.orderType, Equal<Required<SOLine.orderType>>,
                                         And<SOLine.orderNbr, Equal<Required<SOLine.orderNbr>>,
                                         And<SOLine.schedOrderDate, LessEqual<Required<SOLine.schedOrderDate>>,
                                         And<Sub<SOLine.orderQty, SOLine.qtyOnOrders>, Greater<decimal0>>>>>
                                     >.Select(Base, ord.OrderType, ord.OrderNbr, cutOff))
                {
                    if (ln.InventoryID != null)
                        shipItems.Add(ln.InventoryID.Value);
                }
            }
            PXTrace.WriteInformation($"ProcessSeries – {shipItems.Count} item(s) scheduled for shipment before {cutOff:d}");

            if (!shipItems.Any())
                throw new PXException("No shippable lines found for the current filter.");

            /* 5.2 – SeriesDetail rows tied to those items            */
            var details = PXSelectJoin<
                              SeriesDetail,
                              InnerJoin<Series,
                                   On<Series.bookSeriesID, Equal<SeriesDetail.seriesID>>>,
                              Where<Series.bookSeriesCD, Equal<Required<Series.bookSeriesCD>>>>
                         .Select(Base, fExt.UsrBookSeriesCD)
                         .RowCast<SeriesDetail>()
                         .Where(d => d.Bookid != null && shipItems.Contains(d.Bookid.Value))
                         .ToList();

            PXTrace.WriteInformation($"ProcessSeries – {details.Count} SeriesDetail row(s) need evaluation");

            if (!details.Any())
                throw new PXException("No SeriesDetail rows match the items being shipped.");

            /* 5.3 – Preview dialog                                   */
            var sb = new StringBuilder();
            foreach (SeriesDetail det in details)
            {
                Series ser = PXSelect<Series,
                                  Where<Series.bookSeriesID,
                                        Equal<Required<Series.bookSeriesID>>>>
                             .Select(Base, det.SeriesID);
                if (ser.CycleID == null)
                {
                    PXTrace.WriteInformation(
                        $"SeriesDetailID {det.SeriesRowID} skipped – no CycleID in Series");
                    continue;
                }

                var (nextDate, _) = GetNextCycle(det, ser);
                if (nextDate == null) continue;

                string itemCD = PXSelect<InventoryItem,
                                     Where<InventoryItem.inventoryID,
                                           Equal<Required<InventoryItem.inventoryID>>>>
                                 .Select(Base, det.Bookid)
                                 .TopFirst?.InventoryCD?.Trim() ?? det.Bookid.ToString();

                DateTime oldCycle = det.UpcomingCycleDate ?? Base.Accessinfo.BusinessDate.Value;
                DateTime oldShip = det.ShipDate ?? DateTime.MinValue;
                PXTrace.WriteInformation(
                    $"SeriesDetailID {det.SeriesRowID} – " +
                    $"UpcomingCycleDate={oldCycle:MM/dd/yyyy}, ShipDate={oldShip:MM/dd/yyyy}");
                TimeSpan leadTime =  oldCycle - oldShip;                // preserve original offset
                DateTime newShip = nextDate.Value - leadTime;
                PXTrace.WriteInformation(
                    $"Next cycle for {itemCD} is {nextDate:MM/dd/yyyy}, " +
                    $"new ship date will be {newShip:MM/dd/yyyy} \n Lead ${leadTime} " );

                sb.AppendLine($"{itemCD,-15} {det.CycleMajor}/{det.CycleMinor}  " +
                              $"Cycle {oldCycle:MM/dd/yyyy} → {nextDate:MM/dd/yyyy},  " +
                              $"Ship {oldShip:MM/dd/yyyy} → {newShip:MM/dd/yyyy}");
            }

            if (sb.Length == 0)
            {
                PXTrace.WriteInformation("ProcessSeries – no updates required");
                //throw new PXException("No SeriesDetail rows needed an update.");
            }
            if(sb.Length > 0)
            {
                if (Base.Orders.Ask("Confirm Updates", sb.ToString(), MessageButtons.YesNo)
                    != WebDialogResult.Yes)
                {
                    PXTrace.WriteInformation("ProcessSeries – user cancelled");
                    return adapter.Get();
                }
            }

            /* 5.4 – Update SeriesDetail rows directly in DB          */
            foreach (SeriesDetail det in details)
            {
                Series ser = PXSelect<Series,
                                  Where<Series.bookSeriesID,
                                        Equal<Required<Series.bookSeriesID>>>>
                             .Select(Base, det.SeriesID);

                var (nextDate, _) = GetNextCycle(det, ser);
                if (nextDate == null) continue;

                TimeSpan leadTime = TimeSpan.Zero;
                if (det.ShipDate.HasValue && det.UpcomingCycleDate.HasValue)
                    leadTime = det.ShipDate.Value - det.UpcomingCycleDate.Value;

                DateTime? newShip = det.ShipDate.HasValue ? nextDate.Value + leadTime : (DateTime?)null;

                PXDatabase.Update<SeriesDetail>(
                    new PXDataFieldAssign<SeriesDetail.upcomingCycleDate>(nextDate),
                    new PXDataFieldAssign<SeriesDetail.shipDate>(newShip),
                    new PXDataFieldRestrict<SeriesDetail.seriesRowID>(det.SeriesRowID));

                PXTrace.WriteInformation(
                    $"SeriesDetailID {det.SeriesRowID} updated – " +
                    $"UpcomingCycleDate={nextDate:MM/dd/yyyy}, ShipDate={newShip:MM/dd/yyyy}");
            }

            PXTrace.WriteInformation("ProcessSeries – DB updates complete");

            /* 5.5 – Hand off to standard ProcessAll long operation   */
            Base.Actions["ProcessAll"].Press();
            PXTrace.WriteInformation("ProcessSeries – ProcessAll started");

            return adapter.Get();
        }

        /* ───────────────────────────────────────────────────────── */
        /* 6 – Enable / disable button based on filter               */
        /* ───────────────────────────────────────────────────────── */
        protected virtual void SOOrderFilter_RowSelected(
            PXCache sender, PXRowSelectedEventArgs e)
        {
            var row = (SOOrderFilter)e.Row;
            var ext = row?.GetExtension<SOFilterExt>();


            bool isseriesfiltered = ext?.UsrBookSeriesCD != null;

            ProcessSeries.SetEnabled(isseriesfiltered);
            ProcessSeries.SetVisible(isseriesfiltered);

            Base.Orders.SetProcessVisible(!isseriesfiltered);
            Base.Orders.SetProcessAllVisible(!isseriesfiltered);


            PXTrace.WriteInformation(
                $"RowSelected – ProcessSeries button {(isseriesfiltered ? "enabled" : "disabled")}");
        }
    }
}
