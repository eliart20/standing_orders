using System.Collections.Generic;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;

namespace StandingOrders
{
    public class RecycleSeries : PXGraph<RecycleSeries>
    {
        /* ─────────────── data views ─────────────── */

        [PXProcessButton]
        public PXProcessing<Series> SeriesRecords;


        public PXSelect<
            SeriesDetail,
            Where<SeriesDetail.seriesID, Equal<Current<Series.bookSeriesID>>>>
            SeriesDetailRecords;

        /* ─────────────── constructor ─────────────── */

        public RecycleSeries()
        {
            SeriesRecords.SetSelected<Series.selected>(); // Explicitly specify the type argument 
            SeriesRecords.SetProcessDelegate(Process); // wires the toolbar buttons  
            SeriesRecords.SetProcessAllVisible(true); // enables "Process All" button
            SeriesRecords.SetProcessVisible(true); // ensures "Process" button is visible
        }

        /* ─────────────── process logic ─────────────── */

        private static void Process(List<Series> list)
        {
            foreach (Series series in list)
                ProcessSingle(series);
        }

        private static void ProcessSingle(Series series)
        {
            var graph = CreateInstance<RecycleSeries>();

            foreach (SeriesDetail detail in
                     SelectFrom<SeriesDetail>
                     .Where<SeriesDetail.seriesID.IsEqual<@P.AsInt>>.View
                     .Select(graph, series.BookSeriesID))
            {
                /* TODO: update fields as required, e.g.:
                   detail.UpcomingCycleDate = CalcNextDate(detail); */
                var oldShipDate = detail.ShipDate;
                var upComingCycleDate = detail.UpcomingCycleDate;
                PXTrace.WriteInformation(
                    $"Processing SeriesDetail: SeriesID={detail.SeriesID}, ShipDate={oldShipDate}, UpcomingCycleDate={upComingCycleDate}");
                graph.Caches[typeof(SeriesDetail)].Update(detail);
            }

            graph.Actions.PressSave();
        }
    }
}