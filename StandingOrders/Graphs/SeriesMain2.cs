using PX.Data;
using System.Collections;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using PX.Objects.SO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StandingOrders
{
    // 1) Declare Series as the primary DAC
    public class SeriesMain2 : PXGraph<SeriesMain2, Series>
    {
        // ────────────────────────────────────────────────────────────────
        // Standard toolbar actions
        // ────────────────────────────────────────────────────────────────
        public PXSave<Series> Save;
        public PXCancel<Series> Cancel;
        public PXInsert<Series> InsertSeries;

        // ────────────────────────────────────────────────────────────────
        // Data views
        // ────────────────────────────────────────────────────────────────
        public PXSelect<Series> MasterView;

        public PXSelect<SeriesDetail,
            Where<SeriesDetail.seriesID, Equal<Current<Series.bookSeriesID>>>> DetailsView;

        public PXSelect<Cycle> Cycles;
        public PXSelect<CycleDetail> CycleDetails;

        // ────────────────────────────────────────────────────────────────
        // Constants
        // ────────────────────────────────────────────────────────────────
        private const int BATCH_SIZE = 50;          // option 2 – commit cadence

        // ────────────────────────────────────────────────────────────────
        // NEW: Sync SO Orders button
        // ────────────────────────────────────────────────────────────────
        public PXAction<Series> SyncOrders;
        [PXButton(CommitChanges = true)]
        [PXUIField(DisplayName = "Sync SO Orders")]
        protected IEnumerable syncOrders(PXAdapter adapter)
        {
            SyncOrdersWithSeries();
            return adapter.Get();
        }

        // ────────────────────────────────────────────────────────────────
        // Business events and helpers (original code – unchanged)
        // ────────────────────────────────────────────────────────────────
        #region Original graph logic (FieldUpdated, RowSelected, etc.)
        // … (unchanged code from the original SeriesMain2.cs) …
        #endregion

        // ────────────────────────────────────────────────────────────────
        // Persist override – always keep orders in sync
        // ────────────────────────────────────────────────────────────────
        public override void Persist()
        {
            base.Persist();            // first write the Series & details
            SyncOrdersWithSeries();    // then propagate changes to orders
        }

        // ────────────────────────────────────────────────────────────────
        // Core synchronisation routine
        // ────────────────────────────────────────────────────────────────

        private void SyncOrdersWithSeries()
        {
            Series current = MasterView.Current;
            if (current?.BookSeriesCD == null)
                return;   // nothing selected

            // Capture the keys *before* hopping to the long op
            int seriesID = current.BookSeriesID.Value;
            int seriesCD = current.BookSeriesCD.Value;

            // Fire-and-forget background process (shows a progress bar)
            PXLongOperation.StartOperation(this, () =>
            {
                // Use a fresh graph so we don’t hold UI caches/locks
                SeriesMain2 graph = PXGraph.CreateInstance<SeriesMain2>();
                graph.ExecuteSeriesSync(seriesID, seriesCD);
            });
        }

        //───────────────────────────────────────────────────────────────
        // Actual synchronisation logic (now hybrid – options 1, 2, 3)
        //───────────────────────────────────────────────────────────────
        private void ExecuteSeriesSync(int seriesID, int seriesCD)
        {
            // 1) Get Series & its details
            Series series = SelectFrom<Series>
                .Where<Series.bookSeriesID.IsEqual<@P.AsInt>>
                .View.Select(this, seriesID)
                .TopFirst;
            if (series == null) return;

            List<SeriesDetail> seriesDetails = SelectFrom<SeriesDetail>
                    .Where<SeriesDetail.seriesID.IsEqual<@P.AsInt>>
                    .View.Select(this, seriesID)
                    .RowCast<SeriesDetail>()
                    .Where(d => d.Bookid != null)
                    .ToList();

            Dictionary<int, SeriesDetail> detailByInventory = seriesDetails.ToDictionary(d => d.Bookid.Value);

            // ───── Option 3: bulk ship-date update (set-based SQL) ─────
            BulkUpdateShipDates(seriesCD, seriesDetails);

            // 2) Find all orders linked to this series
            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            // ───── Option 1: reuse single graph; Option 2: batched commits ─────
            SOOrderEntry orderGraph = PXGraph.CreateInstance<SOOrderEntry>();
            int pendingModifications = 0;

            foreach (SOOrder orderStub in orders)
            {
                // load order
                orderGraph.Document.Current = orderGraph.Document
                    .Search<SOOrder.orderNbr>(orderStub.OrderNbr, orderStub.OrderType);
                if (orderGraph.Document.Current == null) continue;

                Dictionary<int, SOLine> lineByInventory = orderGraph.Transactions.Select()
                    .RowCast<SOLine>()
                    .Where(l => l.InventoryID != null)
                    .ToDictionary(l => l.InventoryID.Value);

                bool changed = false;

                // ───── Add / Update ─────
                foreach (SeriesDetail det in seriesDetails)
                {
                    int invID = det.Bookid.Value;
                    DateTime? targetDate = det.ShipDate;

                    if (lineByInventory.TryGetValue(invID, out SOLine line))
                    {
                        if (!IsProcessed(line) && line.SchedShipDate != targetDate)
                        {
                            line.SchedOrderDate = targetDate;
                            line.SchedShipDate = targetDate;
                            orderGraph.Transactions.Update(line);
                            changed = true;
                        }
                    }
                    else
                    {
                        SOLine newLine = new SOLine
                        {
                            InventoryID = invID,
                            OrderQty = 1m,
                            SchedOrderDate = targetDate,
                            SchedShipDate = targetDate,
                            ShipComplete = SOShipComplete.BackOrderAllowed
                        };
                        orderGraph.Transactions.Insert(newLine);
                        changed = true;
                    }
                }

                // ───── Remove ─────
                foreach (SOLine line in lineByInventory.Values)
                {
                    if (detailByInventory.ContainsKey(line.InventoryID.Value))
                        continue;
                    if (!IsProcessed(line))
                    {
                        orderGraph.Transactions.Delete(line);
                        changed = true;
                    }
                }

                // ───── Commit control ─────
                if (changed)
                {
                    pendingModifications++;
                    if (pendingModifications >= BATCH_SIZE)
                    {
                        TryPersist(orderGraph);
                        orderGraph.Clear();          // flush caches for next batch
                        pendingModifications = 0;
                    }
                }
                else
                {
                    orderGraph.Clear();              // nothing changed – release caches
                }
            }

            // final flush
            if (pendingModifications > 0)
            {
                TryPersist(orderGraph);
                orderGraph.Clear();
            }
        }

        //───────────────────────────────────────────────────────────────
        // Bulk ship-date updater (option 3)
        //───────────────────────────────────────────────────────────────
        private void BulkUpdateShipDates(int seriesCD, IEnumerable<SeriesDetail> details)
        {
            if (details == null) return;

            // 1) collect every SO order that belongs to the series
            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, seriesCD)
                .RowCast<SOOrder>();

            // 2) issue one UPDATE per (order, inventory) pair
            foreach (SOOrder hdr in orders)
            {
                foreach (SeriesDetail det in details)
                {
                    if (det.Bookid == null || det.ShipDate == null) continue;

                    PXDatabase.Update<SOLine>(
                        new PXDataFieldAssign<SOLine.schedOrderDate>(PXDbType.DateTime, det.ShipDate),
                        new PXDataFieldAssign<SOLine.schedShipDate>(PXDbType.DateTime, det.ShipDate),

                        new PXDataFieldRestrict<SOLine.orderType>(PXDbType.Char, 2, hdr.OrderType),
                        new PXDataFieldRestrict<SOLine.orderNbr>(PXDbType.NVarChar, 15, hdr.OrderNbr),
                        new PXDataFieldRestrict<SOLine.inventoryID>(PXDbType.Int, det.Bookid),
                        new PXDataFieldRestrict<SOLine.completed>(PXDbType.Bit, false)
                    );
                }
            }
        }

        //───────────────────────────────────────────────────────────────
        // Helper: safe persist with error logging
        //───────────────────────────────────────────────────────────────
        private static void TryPersist(SOOrderEntry g)
        {
            try
            {
                g.Persist();                     // cheaper than Save.Press()
            }
            catch (PXException ex)
            {
                PXTrace.WriteError($"Series sync save failed: {ex.Message}");
            }
        }

        private static bool IsProcessed(SOLine line)
        {
            bool completed = line.Completed == true;
            bool shipped = (line.ShippedQty ?? 0m) > 0m;
            bool noOpen = (line.OpenQty ?? 0m) == 0m;
            return completed || shipped || noOpen;
        }

        //───────────────────────────────────────────────────────────────
        // ↓ Original event handlers – unchanged – keep existing logic ↓
        //───────────────────────────────────────────────────────────────
        protected void _(Events.FieldUpdated<SeriesDetail, SeriesDetail.cycleMajor> e)
        {
            if (e.Row == null) return;

            // Clear CycleMinor and upcoming fields when major changes
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

            CycleDetail upcomingCycle = SelectFrom<CycleDetail>
                .Where<CycleDetail.cycleID.IsEqual<@P.AsInt>
                    .And<CycleDetail.cycleMajor.IsEqual<@P.AsString>>
                    .And<CycleDetail.cycleMinor.IsEqual<@P.AsString>>
                    .And<CycleDetail.date.IsGreaterEqual<AccessInfo.businessDate.FromCurrent>>>
                .OrderBy<Asc<CycleDetail.date>>
                .View.SelectSingleBound(this, null,
                    MasterView.Current.CycleID,
                    row.CycleMajor,
                    row.CycleMinor);

            if (upcomingCycle != null)
            {
                cache.SetValueExt<SeriesDetail.upcomingCycleID>(row, upcomingCycle.CycleDetailID);
                cache.SetValueExt<SeriesDetail.upcomingCycleDate>(row, upcomingCycle.Date);

                if (setship)
                {
                    int leadTime = MasterView.Current.DefaultLeadTime ?? 0;
                    cache.SetValueExt<SeriesDetail.shipDate>(row, upcomingCycle.Date?.AddDays(-leadTime));
                }
            }
            else
            {
                PXTrace.WriteInformation(
                    $"DEBUG: No upcoming cycle found for Major={row.CycleMajor}, Minor={row.CycleMinor}");
            }
        }

        protected void _(Events.RowSelected<SeriesDetail> e)
        {
            if (e.Row == null || MasterView.Current == null) return;

            if (MasterView.Current.CycleID != null &&
                !string.IsNullOrEmpty(e.Row.CycleMajor) &&
                !string.IsNullOrEmpty(e.Row.CycleMinor))
            {
                bool needsUpdate = e.Row.UpcomingCycleDate == null ||
                                   e.Row.UpcomingCycleDate < this.Accessinfo.BusinessDate;

                if (needsUpdate)
                {
                    PopulateUpcomingCycleFields(e.Cache, e.Row, true);
                }
            }

            PXTrace.WriteInformation(
                $"DEBUG ↓  Major={e.Row.CycleMajor}  Minor={e.Row.CycleMinor}  " +
                $"ID={e.Row.UpcomingCycleID}  Date={e.Row.UpcomingCycleDate:d}");
        }

        protected void _(Events.FieldUpdated<Series, Series.cycleID> e)
        {
            if (e.Row == null) return;

            foreach (SeriesDetail det in DetailsView.Select())
            {
                DetailsView.Delete(det);
            }

            DetailsView.View.RequestRefresh();
        }

        protected void _(Events.RowSelected<Series> e)
        {
            if (e.Row == null) return;

            bool refreshUpcomingDates = false;

            if (refreshUpcomingDates && e.Row.CycleID != null)
            {
                foreach (SeriesDetail detail in DetailsView.Select())
                {
                    if (!string.IsNullOrEmpty(detail.CycleMajor) &&
                        !string.IsNullOrEmpty(detail.CycleMinor))
                    {
                        PopulateUpcomingCycleFields(DetailsView.Cache, detail, false);
                        DetailsView.Cache.MarkUpdated(detail);
                    }
                }
            }
        }

        protected void _(Events.FieldVerifying<SeriesDetail, SeriesDetail.cycleMinor> e)
        {
            if (e.Row == null || MasterView.Current == null || e.NewValue == null) return;

            if (MasterView.Current.CycleID != null && !string.IsNullOrEmpty(e.Row.CycleMajor))
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
                    PXTrace.WriteInformation(
                        $"DEBUG: CycleMinor '{e.NewValue}' does not exist for Major '{e.Row.CycleMajor}' " +
                        $"in CycleID {MasterView.Current.CycleID}. Validation failed.");
                    e.Cancel = true;
                }
            }
        }
    }
}
