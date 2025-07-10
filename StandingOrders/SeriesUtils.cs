using PX.Data;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using PX.Objects.SO;
using System.Linq;

namespace StandingOrders
{
    class SeriesUtils
    {
        /// <summary>
        /// Move a SeriesDetail line to the next matching cycle and shift ShipDate
        /// by the same day-offset it had before.
        /// </summary>
        public void AdvanceUpcomingCycle(int? seriesID, int? inventoryID)
        {
            if (seriesID == null || inventoryID == null) return;

            // header ─ needed for CycleID and default lead time
            Series series =
                PXSelect<Series,
                    Where<Series.bookSeriesID, Equal<Required<Series.bookSeriesID>>>>
                .SelectWindowed(this, 0, 1, seriesID);

            // detail line we are advancing
            SeriesDetail det =
                PXSelect<SeriesDetail,
                    Where<SeriesDetail.seriesID, Equal<Required<SeriesDetail.seriesID>>,
                      And<SeriesDetail.bookid, Equal<Required<SeriesDetail.bookid>>>>>
                .SelectWindowed(this, 0, 1, seriesID, inventoryID);

            if (series == null || det == null ||
                String.IsNullOrEmpty(det.CycleMajor) || String.IsNullOrEmpty(det.CycleMinor))
                return;

            // how many days was ShipDate offset from the old Upcoming date?
            int offset =
                (det.ShipDate.HasValue && det.UpcomingCycleDate.HasValue)
                    ? (det.ShipDate.Value - det.UpcomingCycleDate.Value).Days
                    : -(series.DefaultLeadTime ?? 0);      // fallback: use header lead-time

            // next cycle with the same Major/Minor, later than today (or later than current upcoming date)
            CycleDetail next =
                SelectFrom<CycleDetail>
                    .Where<CycleDetail.cycleID.IsEqual<@P.AsInt>
                      .And<CycleDetail.cycleMajor.IsEqual<@P.AsString>>
                      .And<CycleDetail.cycleMinor.IsEqual<@P.AsString>>
                      .And<CycleDetail.date.IsGreater<@P.AsDateTime>>>
                    .OrderBy<Asc<CycleDetail.date>>
                    .View.SelectSingleBound(this, null,
                            series.CycleID,
                            det.CycleMajor,
                            det.CycleMinor,
                            det.UpcomingCycleDate ?? Accessinfo.BusinessDate);

            if (next == null) return;   // nothing further in the calendar

            // update fields
            det.UpcomingCycleID = next.CycleDetailID;
            det.UpcomingCycleDate = next.Date;
            det.ShipDate = next.Date?.AddDays(offset);

            Caches<SeriesDetail>().Update(det);
            Actions.PressSave();
        }

    }

}
