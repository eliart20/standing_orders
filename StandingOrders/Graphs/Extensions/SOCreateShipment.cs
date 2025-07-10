
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using PX.Objects.SO;
using StandingOrders;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StandingOrders
{
    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public class SOCreateShipment_Extension : PXGraphExtension<SOCreateShipment>
    {
        /* ───────────────────────────────────────────────────────── */
        /* 1 – Add Book‑Series filter to the SOCreateShipment query  */
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
                PXTrace.WriteInformation($"AddCommonFilters – limited SOOrder view to BookSeriesCD {fExt.UsrBookSeriesCD}");
            }
        }

        /* ───────────────────────────────────────────────────────── */
        /* 2 – Hide stock buttons so only our Process Series shows   */
        /* ───────────────────────────────────────────────────────── */
        public override void Initialize()
        {
            Base.Orders.SetProcessWorkflowAction(null, null, null, null);
            PXTrace.WriteInformation("Initialize – stock Process buttons disabled");
        }

        /* ───────────────────────────────────────────────────────── */
        /* 3 – Refresh grid when Book‑Series filter changes          */
        /* ───────────────────────────────────────────────────────── */
        protected void _(
            Events.FieldUpdated<SOOrderFilter, SOFilterExt.usrBookSeriesCD> e)
        {
            Base.Orders.Cache.ClearQueryCache();
            Base.Orders.View.RequestRefresh();
            PXTrace.WriteInformation($"Filter updated – BookSeriesCD = {e.NewValue}");
        }

        /* ───────────────────────────────────────────────────────── */
        /* 4 – Helper: find next cycle date + delta (lead‑time)      */
        /* ───────────────────────────────────────────────────────── */
        private (DateTime? nextDate, TimeSpan delta) GetNextCycle(
            SeriesDetail det, Series series)
        {
            if (series?.CycleID == null ||
                det.CycleMajor == null ||
                det.CycleMinor == null)
                return (null, TimeSpan.Zero);

            DateTime curr = det.UpcomingCycleDate ?? Base.Accessinfo.BusinessDate.Value;

            CycleDetail next = PXSelect<
                                    CycleDetail,
                                    Where<CycleDetail.cycleID,
                                          Equal<Required<CycleDetail.cycleID>>,
                                      And<CycleDetail.cycleMajor,
                                          Equal<Required<CycleDetail.cycleMajor>>,
                                      And<CycleDetail.cycleMinor,
                                          Equal<Required<CycleDetail.cycleMinor>>,
                                      And<CycleDetail.date,
                                          Greater<Required<CycleDetail.date>>>>>>,
                                    OrderBy<Asc<CycleDetail.date>>>.
                                SelectWindowed(Base, 0, 1,
                                               series.CycleID,
                                               det.CycleMajor,
                                               det.CycleMinor,
                                               curr);

            if (next == null)
                return (null, TimeSpan.Zero);

            TimeSpan delta = next.Date.Value - curr;
            return (next.Date, delta);
        }

        /* ───────────────────────────────────────────────────────── */
        /* 5 – Toolbar button: Process Series                        */
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

            PXTrace.WriteInformation($"ProcessSeries – starting for BookSeriesCD {fExt.UsrBookSeriesCD}");

            /* 5.1 – Collect items being shipped now */
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
                                 And<SOLine.schedOrderDate, LessEqual<Required<SOLine.schedOrderDate>>>>>>
                             .Select(Base, ord.OrderType, ord.OrderNbr, cutOff))
                {
                    if (ln.InventoryID != null) shipItems.Add(ln.InventoryID.Value);
                }
            }
            PXTrace.WriteInformation($"ProcessSeries – {shipItems.Count} InventoryID(s) will ship in this batch");

            if (shipItems.Count == 0)
                throw new PXException("No shippable lines found for the current filter.");

            /* 5.2 – Fetch matching SeriesDetail rows via BookSeriesCD */
            var dets = PXSelectJoin<
                           SeriesDetail,
                           InnerJoin<Series,
                               On<Series.bookSeriesID, Equal<SeriesDetail.seriesID>>>,
                           Where<Series.bookSeriesCD, Equal<Required<Series.bookSeriesCD>>>>
                       .Select(Base, fExt.UsrBookSeriesCD)
                       .RowCast<SeriesDetail>()
                       .Where(d => d.Bookid != null && shipItems.Contains(d.Bookid.Value))
                       .ToList();

            if (dets.Count == 0)
                throw new PXException("No SeriesDetail rows match the items being shipped.");

            PXTrace.WriteInformation($"ProcessSeries – {dets.Count} SeriesDetail row(s) selected");

            /* 5.3 – Build preview dialog */
            var sb = new StringBuilder();
            var detCache = Base.Caches<SeriesDetail>();

            foreach (SeriesDetail det in dets)
            {
                Series series = PXSelect<Series,
                                    Where<Series.bookSeriesID,
                                          Equal<Required<Series.bookSeriesID>>>>
                               .Select(Base, det.SeriesID);
                var (nextDate, delta) = GetNextCycle(det, series);
                if (nextDate == null) continue;

                string itemCD = PXSelect<InventoryItem,
                                     Where<InventoryItem.inventoryID,
                                           Equal<Required<InventoryItem.inventoryID>>>>
                                 .Select(Base, det.Bookid)
                                 .TopFirst?.InventoryCD?.Trim() ?? det.Bookid.ToString();

                DateTime oldCycle = det.UpcomingCycleDate ?? Base.Accessinfo.BusinessDate.Value;
                DateTime oldShip = det.ShipDate ?? DateTime.MinValue;
                DateTime newShip = oldShip.Add(delta);

                sb.AppendLine($"{itemCD,-15} {det.CycleMajor}/{det.CycleMinor}  " +
                              $"Cycle {oldCycle:MM/dd/yyyy} → {nextDate:MM/dd/yyyy},  " +
                              $"Ship {oldShip:MM/dd/yyyy} → {newShip:MM/dd/yyyy}");
            }

            if (sb.Length == 0)
                throw new PXException("No SeriesDetail rows needed an update.");

            if (Base.Orders.Ask("Confirm Updates", sb.ToString(), MessageButtons.YesNo) != WebDialogResult.Yes)
            {
                PXTrace.WriteInformation("ProcessSeries – user cancelled");
                return adapter.Get();
            }

            /* 5.4 – Perform the updates */
            foreach (SeriesDetail det in dets)
            {
                Series series = PXSelect<Series,
                                    Where<Series.bookSeriesID,
                                          Equal<Required<Series.bookSeriesID>>>>
                               .Select(Base, det.SeriesID);

                var (nextDate, delta) = GetNextCycle(det, series);
                if (nextDate == null) continue;

                det.UpcomingCycleDate = nextDate;
                if (det.ShipDate != null)
                    det.ShipDate = det.ShipDate.Value.Add(delta);

                detCache.Update(det);

                PXTrace.WriteInformation(
                    $"Updated SD#{det.SeriesRowID}: UpcomingCycleDate={nextDate:MM/dd/yyyy}, ShipDate={det.ShipDate:MM/dd/yyyy}");
            }

            Base.Actions["Save"].Press();
            PXTrace.WriteInformation("ProcessSeries – SeriesDetail updates saved");

            Base.Actions["ProcessAll"].Press();
            PXTrace.WriteInformation("ProcessSeries – handed off to standard ProcessAll");

            return adapter.Get();
        }

        /* ───────────────────────────────────────────────────────── */
        /* 6 – Enable/disable button depending on filter             */
        /* ───────────────────────────────────────────────────────── */
        protected virtual void SOOrderFilter_RowSelected(PXCache sender, PXRowSelectedEventArgs e)
        {
            var row = (SOOrderFilter)e.Row;
            bool enabled = row?.GetExtension<SOFilterExt>()?.UsrBookSeriesCD != null;

            ProcessSeries.SetEnabled(enabled);
            ProcessSeries.SetVisible(enabled);

            Base.Orders.SetProcessVisible(!enabled);
            Base.Orders.SetProcessAllVisible(!enabled);

            PXTrace.WriteInformation($"RowSelected – ProcessSeries button {(enabled ? "enabled" : "disabled")}");
        }
    }
}
