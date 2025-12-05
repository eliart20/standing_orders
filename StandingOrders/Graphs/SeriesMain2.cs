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
    /// Series maintenance & blanket‑order synchronisation (no batching).
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
        // entry‑point (no screen‑graph capture)
        //───────────────────────────────────────────────────────────────
        private void SyncOrdersWithSeries()
        {
            if (MasterView.Current?.BookSeriesCD == null) return;

            int seriesID = MasterView.Current.BookSeriesID.Value;
            int seriesCD = MasterView.Current.BookSeriesCD.Value;

            PXLongOperation.StartOperation(this, delegate
            {
                var proc = PXGraph.CreateInstance<SeriesMain2>();
                proc.ExecuteSeriesSync(seriesID, seriesCD);
            });
        }

        //───────────────────────────────────────────────────────────────
        // main routine
        //───────────────────────────────────────────────────────────────
        private void ExecuteSeriesSync(int seriesID, int seriesCD)
        {
            PXTrace.WriteInformation($"[SeriesSync] Processing series {seriesCD} ({seriesID})");
            var seriesDetails = SelectFrom<SeriesDetail>
                .Where<SeriesDetail.seriesID.IsEqual<@P.AsInt>>
                .View.Select(this, seriesID)
                .RowCast<SeriesDetail>()
                .Where(d => d.Bookid != null)
                .ToList();

            var detailByInv = seriesDetails.ToDictionary(d => d.Bookid.Value);

            BulkUpdateShipDates(seriesCD, seriesDetails);

            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            foreach (SOOrder hdr in orders)
            {
                PXTrace.WriteInformation($"[SeriesSync] Processing order {hdr.OrderType}-{hdr.OrderNbr}");

                SOOrderEntry g = PXGraph.CreateInstance<SOOrderEntry>();
                g.Document.Current = g.Document.Search<SOOrder.orderNbr>(hdr.OrderNbr, hdr.OrderType);
                if (g.Document.Current == null)
                {
                    PXTrace.WriteWarning($"[SeriesSync] Order {hdr.OrderType}-{hdr.OrderNbr} not found ― skipped.");
                    continue;
                }

                var childLn = GetChildLineNbrs(hdr.OrderType, hdr.OrderNbr);

                /* build inventory map excluding processed & child lines */
                var byInv = g.Transactions.Select()
                    .RowCast<SOLine>()
                    .Where(l => l.InventoryID != null &&
                                !IsProcessed(l) &&
                                !childLn.Contains(l.LineNbr.Value))
                    .ToDictionary(l => l.InventoryID.Value);

                bool changed = false;

                /* add / update */
                foreach (SeriesDetail det in seriesDetails)
                {
                    int invID = det.Bookid.Value;
                    DateTime? tgt = ResolveShipDate(det, hdr, g.Accessinfo.BusinessDate);

                    if (!IsShipDateAllowed(tgt, hdr.OrderDate, g.Accessinfo.BusinessDate))
                    {
                        PXTrace.WriteInformation(
                            $"[SeriesSync] Skipped inventory {invID} ― ship date {tgt:d} precedes order/today.");
                        continue;
                    }

                    if (byInv.TryGetValue(invID, out SOLine ln)) /* ─ update ─ */
                    {
                        if (!Equals(ln.SchedShipDate, tgt))
                        {
                            PXTrace.WriteInformation(
                                $"[SeriesSync] Updating ship date on {invID} line {ln.LineNbr} " +
                                $"from {ln.SchedShipDate:d} to {tgt:d}.");

                            g.Transactions.Cache.SetValueExt<SOLine.schedOrderDate>(ln, tgt);
                            g.Transactions.Cache.SetValueExt<SOLine.schedShipDate>(ln, tgt);
                            g.Transactions.Update(ln);
                            changed = true;
                        }
                    }
                    else                                         /* ─ insert ─ */
                    {
                        try
                        {
                            var newLn = (SOLine)g.Transactions.Cache.CreateInstance();
                            newLn.OrderType = hdr.OrderType;
                            newLn.OrderNbr = hdr.OrderNbr;
                            
                            /* insert first to get line number and defaults */
                            newLn = g.Transactions.Insert(newLn);
                            if (newLn == null)
                            {
                                PXTrace.WriteError(
                                    $"[SeriesSync] Failed to create new SOLine for inventory {invID}.");
                                continue;
                            }

                            /* now set the fields after insert has created the line */
                            g.Transactions.Cache.SetValueExt<SOLine.inventoryID>(newLn, invID);
                            g.Transactions.Cache.SetValueExt<SOLine.orderQty>(newLn, 1m);
                            g.Transactions.Cache.SetValueExt<SOLine.schedOrderDate>(newLn, tgt);
                            g.Transactions.Cache.SetValueExt<SOLine.schedShipDate>(newLn, tgt);
                            g.Transactions.Cache.SetValueExt<SOLine.shipComplete>(
                                newLn, SOShipComplete.BackOrderAllowed);
                            newLn = g.Transactions.Update(newLn);
                            
                            PXTrace.WriteInformation(
                                $"[SeriesSync] Inserted inventory {invID} on new line {newLn.LineNbr}; " +
                                $"ship date {tgt:d}.");
                            changed = true;
                        }
                        catch (Exception ex)
                        {
                            PXTrace.WriteError(
                                $"[SeriesSync] Failed to insert inventory {invID}: {ex.Message}");
                        }
                    }
                }

                /* remove superfluous */
                foreach (SOLine ln in byInv.Values)
                    if (!detailByInv.ContainsKey(ln.InventoryID.Value))
                    {
                        PXTrace.WriteInformation(
                            $"[SeriesSync] Removing inventory {ln.InventoryID} line {ln.LineNbr}.");
                        g.Transactions.Delete(ln);
                        changed = true;
                    }

                if (changed)
                    TryPersist(g);
            }
        }

        //───────────────────────────────────────────────────────────────
        // per‑order ship‑date updater
        //───────────────────────────────────────────────────────────────
        private void BulkUpdateShipDates(int seriesCD, IEnumerable<SeriesDetail> details)
        {
            if (details == null) return;

            var detByInv = details.ToDictionary(d => d.Bookid.Value);

            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            foreach (SOOrder hdr in orders)
            {
                SOOrderEntry g = PXGraph.CreateInstance<SOOrderEntry>();
                g.Document.Current = g.Document.Search<SOOrder.orderNbr>(hdr.OrderNbr, hdr.OrderType);
                if (g.Document.Current == null) continue;

                var childLn = GetChildLineNbrs(hdr.OrderType, hdr.OrderNbr);

                DateTime? minDate = null;
                bool changed = false;

                foreach (SOLine ln in g.Transactions.Select().RowCast<SOLine>())
                {
                    if (ln.Completed == true || childLn.Contains(ln.LineNbr.Value)) continue;

                    DateTime? tgt = null;
                    if (ln.InventoryID != null &&
                        detByInv.TryGetValue(ln.InventoryID.Value, out SeriesDetail det))
                    {
                        tgt = ResolveShipDate(det, hdr, g.Accessinfo.BusinessDate);

                        if (ln.SchedShipDate != null && tgt != null &&
                            Math.Abs((tgt.Value - ln.SchedShipDate.Value).TotalDays) > 60)
                        {
                            PXTrace.WriteInformation(
                                $"[BulkUpdate] Skipped {ln.InventoryID} on order {hdr.OrderNbr} ― 60‑day rule.");
                            continue; // outside 60‑day window
                        }
                    }

                    if (!IsShipDateAllowed(tgt, hdr.OrderDate, g.Accessinfo.BusinessDate))
                    {
                        PXTrace.WriteInformation(
                            $"[BulkUpdate] Skipped {ln.InventoryID} on order {hdr.OrderNbr} ― " +
                            $"ship date {tgt:d} precedes order/today.");
                        continue;
                    }

                    if (!Equals(ln.SchedShipDate, tgt))
                    {
                        PXTrace.WriteInformation(
                            $"[BulkUpdate] Updating ship date on order {hdr.OrderNbr} " +
                            $"item {ln.InventoryID} from {ln.SchedShipDate:d} to {tgt:d}.");

                        g.Transactions.Cache.SetValueExt<SOLine.schedOrderDate>(ln, tgt);
                        g.Transactions.Cache.SetValueExt<SOLine.schedShipDate>(ln, tgt);
                        g.Transactions.Update(ln);
                        changed = true;
                    }

                    DateTime? candidate = tgt ?? ln.SchedOrderDate;
                    if (candidate != null && (minDate == null || candidate < minDate))
                        minDate = candidate;
                }

                if (changed && minDate != null &&
                    !Equals(g.Document.Current.MinSchedOrderDate, minDate))
                {
                    g.Document.Cache.SetValueExt<SOOrder.minSchedOrderDate>(g.Document.Current, minDate);
                }

                if (changed)
                    TryPersist(g);
            }
        }

        //───────────────────────────────────────────────────────────────
        // helpers
        //───────────────────────────────────────────────────────────────
        private static DateTime? ResolveShipDate(SeriesDetail det,
                                                 SOOrder hdr,
                                                 DateTime? businessDate)
            => det?.ShipDate ?? businessDate;

        private static bool IsShipDateAllowed(DateTime? tgt,
                                              DateTime? orderDate,
                                              DateTime? businessDate)
        {
            if (tgt == null) return true;
            DateTime min = (businessDate ?? DateTime.Today).Date;
            if (orderDate != null && orderDate.Value.Date > min)
                min = orderDate.Value.Date;
            return tgt.Value.Date >= min;
        }

        private HashSet<int> GetChildLineNbrs(string blanketType, string blanketNbr) =>
            SelectFrom<SOLine>
                .Where<SOLine.blanketType.IsEqual<@P.AsString>
                    .And<SOLine.blanketNbr.IsEqual<@P.AsString>>
                    .And<SOLine.blanketLineNbr.IsNotNull>>
                .View.Select(this, blanketType, blanketNbr)
                .RowCast<SOLine>()
                .Select(l => l.BlanketLineNbr.Value)
                .ToHashSet();

        private static bool IsProcessed(SOLine l) =>
            l.Completed == true ||
            (l.ShippedQty ?? 0m) > 0m ||
            (l.OpenQty ?? 0m) == 0m;

        private static void TryPersist(SOOrderEntry g)
        {
            try
            {
                g.Persist();
                PXTrace.WriteInformation(
                    $"[SeriesSync] Persisted order {g.Document.Current.OrderType}-{g.Document.Current.OrderNbr}.");
            }
            catch (PXException ex)
            {
                PXTrace.WriteError(
                    $"[SeriesSync] Save failed for order {g.Document.Current.OrderType}-{g.Document.Current.OrderNbr}: {ex.Message}");
            }
        }

        //───────────────────────────────────────────────────────────────
        // event handlers (unchanged)
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

        private void PopulateUpcomingCycleFields(PXCache cache,
                                                 SeriesDetail row,
                                                 bool setship)
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
                    cache.SetValueExt<SeriesDetail.shipDate>(row,
                        upcoming.Date?.AddDays(-lead));
                }
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

                if (exists == null) e.Cancel = true;
            }
        }
    }
}

