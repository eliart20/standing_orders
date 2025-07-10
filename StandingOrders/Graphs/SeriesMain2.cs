using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using System;

namespace StandingOrders
{
    // 1) Declare Series as the primary DAC
    public class SeriesMain2 : PXGraph<SeriesMain2, Series>
    {
        // standard toolbar actions
        public PXSave<Series> Save;
        public PXCancel<Series> Cancel;
        public PXInsert<Series> InsertSeries;

        // master view of the header
        public PXSelect<Series> MasterView;

        // the detail grid, filtered by the current (even unsaved) BookSeriesID
        public PXSelect<SeriesDetail,
            Where<SeriesDetail.seriesID, Equal<Current<Series.bookSeriesID>>>> DetailsView;

        // supporting caches for lookup/data loading
        public PXSelect<Cycle> Cycles;
        public PXSelect<CycleDetail> CycleDetails;

        //────────────────────────────────────────────────────────────────────
        // When CycleMajor changes, clear dependent fields
        //────────────────────────────────────────────────────────────────────
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