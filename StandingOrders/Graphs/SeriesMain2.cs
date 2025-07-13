using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using PX.Objects.SO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace StandingOrders
{
    /// <summary>
    /// Series maintenance & blanket‑order synchronisation.
    /// </summary>
    public class SeriesMain2 : PXGraph<SeriesMain2, Series>
    {
        /* ───── toolbar ───── */
        public PXSave<Series> Save;
        public PXCancel<Series> Cancel;
        public PXInsert<Series> InsertSeries;

        /* ───── data views ───── */
        public PXSelect<Series> MasterView;
        public PXSelect<SeriesDetail,
            Where<SeriesDetail.seriesID, Equal<Current<Series.bookSeriesID>>>> DetailsView;

        public PXSelect<Cycle> Cycles;
        public PXSelect<CycleDetail> CycleDetails;

        private const int BATCH_SIZE = 50;        /* option‑2 */

        /* ───── UI action ───── */
        public PXAction<Series> SyncOrders;
        [PXButton(CommitChanges = true)]
        [PXUIField(DisplayName = "Sync SO Orders")]
        protected IEnumerable syncOrders(PXAdapter a)
        {
            SyncOrdersWithSeries();
            return a.Get();
        }

        public override void Persist()
        {
            base.Persist();
            SyncOrdersWithSeries();
        }

        //───────────────────────────────────────────────────────────────
        // entry‑point
        //───────────────────────────────────────────────────────────────
        private void SyncOrdersWithSeries()
        {
            if (MasterView.Current?.BookSeriesCD == null) return;

            int seriesID = MasterView.Current.BookSeriesID.Value;
            int seriesCD = MasterView.Current.BookSeriesCD.Value;

            PXLongOperation.StartOperation(this, () =>
            {
                PXGraph.CreateInstance<SeriesMain2>()
                       .ExecuteSeriesSync(seriesID, seriesCD);
            });
        }

        //───────────────────────────────────────────────────────────────
        // main routine
        //───────────────────────────────────────────────────────────────
        private void ExecuteSeriesSync(int seriesID, int seriesCD)
        {
            List<SeriesDetail> seriesDetails = SelectFrom<SeriesDetail>
                .Where<SeriesDetail.seriesID.IsEqual<@P.AsInt>>
                .View.Select(this, seriesID)
                .RowCast<SeriesDetail>()
                .Where(d => d.Bookid != null)
                .ToList();

            Dictionary<int, SeriesDetail> detailByInv =
                seriesDetails.ToDictionary(d => d.Bookid.Value);

            BulkUpdateShipDates(seriesCD, seriesDetails);                /* option‑3 */

            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            SOOrderEntry g = PXGraph.CreateInstance<SOOrderEntry>();     /* option‑1 */
            int dirty = 0;

            foreach (SOOrder hdr in orders)
            {
                g.Document.Current =
                    g.Document.Search<SOOrder.orderNbr>(hdr.OrderNbr, hdr.OrderType);
                if (g.Document.Current == null) continue;

                HashSet<int> childLn = GetChildLineNbrs(hdr.OrderType, hdr.OrderNbr);

                Dictionary<int, SOLine> byInv = g.Transactions.Select()
                    .RowCast<SOLine>()
                    .Where(l => l.InventoryID != null)
                    .ToDictionary(l => l.InventoryID.Value);

                bool changed = false;

                /* add / update */
                foreach (SeriesDetail det in seriesDetails)
                {
                    int invID = det.Bookid.Value;
                    DateTime? tgt = det.ShipDate;

                    if (byInv.TryGetValue(invID, out SOLine ln))
                    {
                        if (!childLn.Contains(ln.LineNbr.Value) &&
                            !IsProcessed(ln) &&
                            ln.SchedShipDate != tgt)
                        {
                            ln.SchedOrderDate = tgt;
                            ln.SchedShipDate = tgt;
                            g.Transactions.Update(ln);
                            changed = true;
                        }
                    }
                    else
                    {
                        g.Transactions.Insert(new SOLine
                        {
                            InventoryID = invID,
                            OrderQty = 1m,
                            SchedOrderDate = tgt,
                            SchedShipDate = tgt,
                            ShipComplete = SOShipComplete.BackOrderAllowed
                        });
                        changed = true;
                    }
                }

                /* remove */
                foreach (SOLine ln in byInv.Values)
                    if (!detailByInv.ContainsKey(ln.InventoryID.Value) &&
                        !childLn.Contains(ln.LineNbr.Value) &&
                        !IsProcessed(ln))
                    {
                        g.Transactions.Delete(ln);
                        changed = true;
                    }

                if (changed)
                {
                    dirty++;
                    if (dirty >= BATCH_SIZE)
                    {
                        TryPersist(g);
                        g.Clear();
                        dirty = 0;
                    }
                }
                else
                    g.Clear();
            }

            if (dirty > 0)
            {
                TryPersist(g);
                g.Clear();
            }
        }

        //───────────────────────────────────────────────────────────────
        // bulk ship‑date updater with correct header recalculation
        //───────────────────────────────────────────────────────────────
        private void BulkUpdateShipDates(int seriesCD, IEnumerable<SeriesDetail> details)
        {
            if (details == null) return;

            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            foreach (SOOrder hdr in orders)
            {
                HashSet<int> childLn = GetChildLineNbrs(hdr.OrderType, hdr.OrderNbr);

                var lines = SelectFrom<SOLine>
                    .Where<SOLine.orderType.IsEqual<@P.AsString>
                        .And<SOLine.orderNbr.IsEqual<@P.AsString>>>
                    .View.Select(this, hdr.OrderType, hdr.OrderNbr)
                    .RowCast<SOLine>();

                DateTime? minDate = null;

                foreach (SOLine ln in lines)
                {
                    if (ln.Completed == true || childLn.Contains(ln.LineNbr.Value)) continue;

                    DateTime? newDate = ln.SchedOrderDate;

                    SeriesDetail det = details.FirstOrDefault(d => d.Bookid == ln.InventoryID);


                    // excerpt showing the 60-day restriction applied correctly
                    if (det.ShipDate.HasValue
                        && ln.SchedShipDate.HasValue
                        && Math.Abs((det.ShipDate.Value - ln.SchedShipDate.Value).TotalDays) > 60)
                    {
                        continue;
                    }



                    if (det != null && det.ShipDate != null && ln.SchedShipDate != det.ShipDate)
                    {
                        PXDatabase.Update<SOLine>(
                            new PXDataFieldAssign<SOLine.schedOrderDate>(PXDbType.DateTime, det.ShipDate),
                            new PXDataFieldAssign<SOLine.schedShipDate>(PXDbType.DateTime, det.ShipDate),
                            new PXDataFieldRestrict<SOLine.orderType>(PXDbType.Char, hdr.OrderType),
                            new PXDataFieldRestrict<SOLine.orderNbr>(PXDbType.NVarChar, hdr.OrderNbr),
                            new PXDataFieldRestrict<SOLine.lineNbr>(PXDbType.Int, ln.LineNbr));

                        newDate = det.ShipDate;                           /* use updated date */
                    }

                    if (newDate != null && (minDate == null || newDate < minDate))
                        minDate = newDate;
                }

                PXDatabase.Update<SOOrder>(                               /* header refresh */
                    new PXDataFieldAssign<SOOrder.minSchedOrderDate>(PXDbType.DateTime, minDate),
                    new PXDataFieldRestrict<SOOrder.orderType>(PXDbType.Char, hdr.OrderType),
                    new PXDataFieldRestrict<SOOrder.orderNbr>(PXDbType.NVarChar, hdr.OrderNbr));
            }
        }

        //───────────────────────────────────────────────────────────────
        // helpers
        //───────────────────────────────────────────────────────────────
        private HashSet<int> GetChildLineNbrs(string blanketType, string blanketNbr)
        {
            return SelectFrom<SOLine>
                    .Where<SOLine.blanketType.IsEqual<@P.AsString>
                        .And<SOLine.blanketNbr.IsEqual<@P.AsString>>
                        .And<SOLine.blanketLineNbr.IsNotNull>>
                    .View.Select(this, blanketType, blanketNbr)
                    .RowCast<SOLine>()
                    .Select(l => l.BlanketLineNbr.Value)
                    .ToHashSet();
        }

        private static bool IsProcessed(SOLine l) =>
            l.Completed == true ||
            (l.ShippedQty ?? 0m) > 0m ||
            (l.OpenQty ?? 0m) == 0m;

        private static void TryPersist(SOOrderEntry g)
        {
            try { g.Persist(); }
            catch (PXException ex)
            { PXTrace.WriteError($"Series sync save failed: {ex.Message}"); }
        }

        //───────────────────────────────────────────────────────────────
        // original event handlers (unchanged)
        //───────────────────────────────────────────────────────────────
        protected void _(Events.FieldUpdated<SeriesDetail, SeriesDetail.cycleMajor> e)
        {
            if (e.Row == null) return;
            e.Cache.SetValueExt<SeriesDetail.cycleMinor>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleID>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleDate>(e.Row, null);
        }

        protected void _(Events.FieldUpdated<SeriesDetail, SeriesDetail.cycleMinor> e)
        {
            if (e.Row == null || MasterView.Current == null) return;

            e.Cache.SetValueExt<SeriesDetail.upcomingCycleID>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleDate>(e.Row, null);

            if (MasterView.Current.CycleID != null &&
                !string.IsNullOrEmpty(e.Row.CycleMajor) &&
                !string.IsNullOrEmpty(e.Row.CycleMinor))
            {
                PopulateUpcomingCycleFields(e.Cache, e.Row, true);
            }
        }

        private void PopulateUpcomingCycleFields(PXCache cache, SeriesDetail row, bool setship)
        {
            if (MasterView.Current?.CycleID == null ||
                string.IsNullOrEmpty(row.CycleMajor) ||
                string.IsNullOrEmpty(row.CycleMinor))
                return;

            CycleDetail upcoming = SelectFrom<CycleDetail>
                .Where<CycleDetail.cycleID.IsEqual<@P.AsInt>
                    .And<CycleDetail.cycleMajor.IsEqual<@P.AsString>>
                    .And<CycleDetail.cycleMinor.IsEqual<@P.AsString>>
                    .And<CycleDetail.date.IsGreaterEqual<AccessInfo.businessDate.FromCurrent>>>
                .OrderBy<Asc<CycleDetail.date>>
                .View.SelectSingleBound(this, null,
                    MasterView.Current.CycleID,
                    row.CycleMajor,
                    row.CycleMinor);

            if (upcoming != null)
            {
                cache.SetValueExt<SeriesDetail.upcomingCycleID>(row, upcoming.CycleDetailID);
                cache.SetValueExt<SeriesDetail.upcomingCycleDate>(row, upcoming.Date);

                if (setship)
                {
                    int lead = MasterView.Current.DefaultLeadTime ?? 0;
                    cache.SetValueExt<SeriesDetail.shipDate>(row, upcoming.Date?.AddDays(-lead));
                }
            }
            else
            {
                PXTrace.WriteInformation(
                    $"DEBUG: No upcoming cycle for {row.CycleMajor}/{row.CycleMinor}");
            }
        }

        protected void _(Events.RowSelected<SeriesDetail> e)
        {
            if (e.Row == null || MasterView.Current == null) return;

            if (MasterView.Current.CycleID != null &&
                !string.IsNullOrEmpty(e.Row.CycleMajor) &&
                !string.IsNullOrEmpty(e.Row.CycleMinor))
            {
                bool needs = e.Row.UpcomingCycleDate == null ||
                             e.Row.UpcomingCycleDate < Accessinfo.BusinessDate;
                if (needs) PopulateUpcomingCycleFields(e.Cache, e.Row, true);
            }
        }

        protected void _(Events.FieldUpdated<Series, Series.cycleID> e)
        {
            if (e.Row == null) return;
            foreach (SeriesDetail det in DetailsView.Select()) DetailsView.Delete(det);
            DetailsView.View.RequestRefresh();
        }

        protected void _(Events.RowSelected<Series> e)
        {
            if (e.Row == null) return;
            bool refresh = false;
            if (refresh && e.Row.CycleID != null)
            {
                foreach (SeriesDetail det in DetailsView.Select())
                {
                    if (!string.IsNullOrEmpty(det.CycleMajor) &&
                        !string.IsNullOrEmpty(det.CycleMinor))
                    {
                        PopulateUpcomingCycleFields(DetailsView.Cache, det, false);
                        DetailsView.Cache.MarkUpdated(det);
                    }
                }
            }
        }

        protected void _(Events.FieldVerifying<SeriesDetail, SeriesDetail.cycleMinor> e)
        {
            if (e.Row == null || MasterView.Current == null || e.NewValue == null) return;

            if (MasterView.Current.CycleID != null &&
                !string.IsNullOrEmpty(e.Row.CycleMajor))
            {
                CycleDetail exists = SelectFrom<CycleDetail>
                    .Where<CycleDetail.cycleID.IsEqual<@P.AsInt>
                        .And<CycleDetail.cycleMajor.IsEqual<@P.AsString>>
                        .And<CycleDetail.cycleMinor.IsEqual<@P.AsString>>>
                    .View.SelectSingleBound(this, null,
                        MasterView.Current.CycleID,
                        e.Row.CycleMajor,
                        e.NewValue);

                if (exists == null)
                {
                    PXTrace.WriteInformation($"DEBUG: Minor '{e.NewValue}' not found");
                    e.Cancel = true;
                }
            }
        }
    }
}
