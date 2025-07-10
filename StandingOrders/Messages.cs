using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PX.Data;
using PX.Common;


namespace StandingOrders
{
     [PXLocalizable]
    public static class STMessages           // <- localizable constants
    {
        public const string OrderNotFound = "Sales Order {0}-{1} was not found.";
        public const string NoOpenQtyForItem = "Order {0}-{1} has no open quantity for item {2}.";
        public const string SiteCannotBeBlank = "Site cannot be determined for shipment.";
        public const string SeriesNotSelected = "No Book Series selected.";
        public const string NoItemsToProcessForSeries = "No lines to process for Book Series {0}.";
        public const string NoLinesToProcess = "No lines to process for the current filter.";



    }
}
