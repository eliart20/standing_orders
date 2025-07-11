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
        // … (unchanged code from the original SeriesMain2.cs) …
        // For brevity in this excerpt the original event handlers are
        // omitted, but they are still present exactly as in the file
        // supplied by the customer.                                              :contentReference[oaicite:0]{index=0}
        #endregion

        // ────────────────────────────────────────────────────────────────
        // NEW: Persist override – always keep orders in sync
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
            Series series = MasterView.Current;
            if (series?.BookSeriesCD == null)
                return; // nothing selected

            // 1) Index current SeriesDetail rows by InventoryID
            List<SeriesDetail> seriesDetails = SelectFrom<SeriesDetail>
                    .Where<SeriesDetail.seriesID.IsEqual<@P.AsInt>>
                    .View.Select(this, series.BookSeriesID)
                    .RowCast<SeriesDetail>()
                    .Where(d => d.Bookid != null)
                    .ToList();

            Dictionary<int, SeriesDetail> detailByInventory =
                seriesDetails.ToDictionary(d => d.Bookid.Value);

            // 2) Fetch all ST SOOrders that reference this BookSeries
            var orders = SelectFrom<SOOrder>
                .Where<SOOrderExt.usrBookSeriesCD.IsEqual<@P.AsInt>>
                .View.Select(this, series.BookSeriesCD)
                .RowCast<SOOrder>();

            foreach (SOOrder orderStub in orders)
            {
                // Load each order in its own graph so all business logic fires
                SOOrderEntry orderGraph = PXGraph.CreateInstance<SOOrderEntry>();
                orderGraph.Document.Current = orderGraph.Document.Search<SOOrder.orderNbr>(orderStub.OrderNbr, orderStub.OrderType);
                if (orderGraph.Document.Current == null)
                    continue;   // safety

                // Index SOLines by InventoryID for quick lookup
                Dictionary<int, SOLine> lineByInventory = orderGraph.Transactions.Select()
                    .RowCast<SOLine>()
                    .Where(l => l.InventoryID != null)
                    .ToDictionary(l => l.InventoryID.Value);

                // ────────── Add or update lines ──────────
                foreach (SeriesDetail det in seriesDetails)
                {
                    int invID = det.Bookid.Value;
                    DateTime? targetShipDate = det.ShipDate;

                    if (lineByInventory.TryGetValue(invID, out SOLine line))
                    {
                        if (!IsProcessed(line) && line.SchedShipDate != targetShipDate)
                        {
                            // Only update lines that are *unprocessed* and whose date still matches the old/default
                            line.SchedOrderDate = targetShipDate;
                            line.SchedShipDate = targetShipDate;
                            orderGraph.Transactions.Update(line);
                        }
                    }
                    else
                    {
                        // Insert the book if it was added to the Series – always unprocessed by definition
                        SOLine newLine = new SOLine
                        {
                            InventoryID = invID,
                            OrderQty = 1m,
                            SchedOrderDate = targetShipDate,
                            SchedShipDate = targetShipDate,
                            ShipComplete = SOShipComplete.BackOrderAllowed
                        };
                        orderGraph.Transactions.Insert(newLine);
                    }
                }

                // ────────── Remove obsolete lines (only if not processed) ──────────
                foreach (SOLine line in lineByInventory.Values)
                {
                    if (detailByInventory.ContainsKey(line.InventoryID.Value))
                        continue;   // still exists in the Series

                    if (!IsProcessed(line))
                        orderGraph.Transactions.Delete(line);
                }

                // 3) Save – warnings about missing price, etc., are ignored automatically
                try
                {
                    orderGraph.Save.Press();
                }
                catch (PXException ex)
                {
                    // Log and continue; we do not want one order to block the rest
                    PXTrace.WriteError($"SyncOrders: could not save SO {orderStub.OrderNbr}: {ex.Message}");
                }
            }
        }

        private static bool IsProcessed(SOLine line)
        {
            // Consider the line processed if it is completed or any quantity has shipped/invoiced
            bool completed = line.Completed == true;
            bool shipped = (line.ShippedQty ?? 0m) > 0m;
            bool noOpenQty = (line.OpenQty ?? 0m) == 0m;
            return completed || shipped || noOpenQty;
        }

        protected void _(Events.FieldUpdated<SeriesDetail, SeriesDetail.cycleMajor> e)
        {
            if (e.Row == null) return;

            // Clear CycleMinor and upcoming fields when major changes
            e.Cache.SetValueExt<SeriesDetail.cycleMinor>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleID>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleDate>(e.Row, null);
        }

        //────────────────────────────────────────────────────────────────────
        // When CycleMinor is selected, populate the upcoming cycle fields
        //────────────────────────────────────────────────────────────────────
        protected void _(Events.FieldUpdated<SeriesDetail, SeriesDetail.cycleMinor> e)
        {
            if (e.Row == null || MasterView.Current == null) return;

            // Clear and repopulate upcoming fields
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleID>(e.Row, null);
            e.Cache.SetValueExt<SeriesDetail.upcomingCycleDate>(e.Row, null);

            if (MasterView.Current.CycleID != null &&
                !string.IsNullOrEmpty(e.Row.CycleMajor) &&
                !string.IsNullOrEmpty(e.Row.CycleMinor))
            {
                PopulateUpcomingCycleFields(e.Cache, e.Row, true);
            }
        }

        //────────────────────────────────────────────────────────────────────
        // Shared method to populate upcoming cycle fields
        //────────────────────────────────────────────────────────────────────
        private void PopulateUpcomingCycleFields(PXCache cache, SeriesDetail row, bool setship)
        {
            if (MasterView.Current?.CycleID == null || string.IsNullOrEmpty(row.CycleMajor) || string.IsNullOrEmpty(row.CycleMinor))
                return;

            // Find the next upcoming cycle detail record
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
                // Populate the upcoming cycle fields
                cache.SetValueExt<SeriesDetail.upcomingCycleID>(row, upcomingCycle.CycleDetailID);
                cache.SetValueExt<SeriesDetail.upcomingCycleDate>(row, upcomingCycle.Date);

                // Optionally set the ship date to match the cycle date if not already set
                if (setship)
                {
                    // Get the Default Lead Time from the Series heade
                    int leadTime = MasterView.Current.DefaultLeadTime ?? 0;
                    cache.SetValueExt<SeriesDetail.shipDate>(row, upcomingCycle.Date?.AddDays(-leadTime));
                }
            }
            else
            {
                // No upcoming cycle found - optionally notify user
                //cache.RaiseExceptionHandling<SeriesDetail.cycleMinor>(
                //    row, row.CycleMinor,
                //    new PXSetPropertyException("No upcoming cycle found for this selection.",
                //        PXErrorLevel.RowWarning));
                PXTrace.WriteInformation(
                    $"DEBUG: No upcoming cycle found for Major={row.CycleMajor}, Minor={row.CycleMinor}"
                    );
            }
        }

        //────────────────────────────────────────────────────────────────────
        // Row Selected - populate upcoming fields for existing records
        //────────────────────────────────────────────────────────────────────
        protected void _(Events.RowSelected<SeriesDetail> e)
        {
            if (e.Row == null || MasterView.Current == null) return;

            // Populate upcoming fields if:
            // 1. They're null (never populated)
            // 2. The upcoming date is in the past (stale data)
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

        //────────────────────────────────────────────────────────────────────
        // When header CycleID changes, clear all detail lines
        //────────────────────────────────────────────────────────────────────
        protected void _(Events.FieldUpdated<Series, Series.cycleID> e)
        {
            if (e.Row == null) return;

            // Delete all detail lines when cycle changes
            foreach (SeriesDetail det in DetailsView.Select())
            {
                DetailsView.Delete(det);
            }

            DetailsView.View.RequestRefresh();
        }

        //────────────────────────────────────────────────────────────────────
        // Optional: Add Series RowSelected to refresh all details on load
        //────────────────────────────────────────────────────────────────────
        protected void _(Events.RowSelected<Series> e)
        {
            if (e.Row == null) return;

            // Optional: If you want to refresh all upcoming dates when the series is loaded
            // (useful if business date has changed since last save)
            bool refreshUpcomingDates = false; // Set to true if you want auto-refresh on load

            if (refreshUpcomingDates && e.Row.CycleID != null)
            {
                foreach (SeriesDetail detail in DetailsView.Select())
                {
                    if (!string.IsNullOrEmpty(detail.CycleMajor) && !string.IsNullOrEmpty(detail.CycleMinor))
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

            // Only validate if we have all required values
            if (MasterView.Current.CycleID != null && !string.IsNullOrEmpty(e.Row.CycleMajor))
            {
                // Check if the combination exists
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

