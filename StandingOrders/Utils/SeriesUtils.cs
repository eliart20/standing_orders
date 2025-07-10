using PX.Data;
using PX.Objects.SO;
using PX.Objects.IN;
using System.Linq;

namespace YourNamespace
{
    public static class ChildOrderHelper
    {
        /// <summary>
        /// Creates a child sales order that contains only the line for <paramref name="inventoryID"/>.
        /// </summary>
        /// <param name="blanketOrderType">Usually "BL"</param>
        /// <param name="blanketOrderNbr">The parent blanket order’s number</param>
        /// <param name="inventoryID">InventoryID of the item to copy</param>
        /// <returns>The new child SO order record</returns>
        public static SOOrder CreateChildOrder(string blanketOrderType, string blanketOrderNbr, int? inventoryID)
        {
            // 1. Load parent
            var parentGraph = PXGraph.CreateInstance<SOOrderEntry>();
            SOOrder parent = PXSelect<
                                SOOrder,
                                Where<SOOrder.orderType, Equal<Required<SOOrder.orderType>>,
                                  And<SOOrder.orderNbr, Equal<Required<SOOrder.orderNbr>>>>>
                             .Select(parentGraph, blanketOrderType, blanketOrderNbr);

            if (parent == null)
            {
                string errorMessage = $"Blanket order {blanketOrderType} {blanketOrderNbr} not found.";

                throw new PXException(errorMessage);
            }

            // 2. Find the line with this item
            SOLine srcLine = PXSelect<
                                SOLine,
                                Where<SOLine.orderType, Equal<Required<SOLine.orderType>>,
                                  And<SOLine.orderNbr, Equal<Required<SOLine.orderNbr>>,
                                  And<SOLine.inventoryID, Equal<Required<SOLine.inventoryID>>>>>>
                             .Select(parentGraph, parent.OrderType, parent.OrderNbr, inventoryID)
                             .FirstOrDefault();

            if (srcLine == null)
            {
                string errorMessage = $"Item {inventoryID} not found on blanket order {blanketOrderNbr}.";
                throw new PXException(errorMessage);
            }

            // 3. Create child order
            var childGraph = PXGraph.CreateInstance<SOOrderEntry>();

            SOOrder child = new SOOrder
            {
                OrderType = SOOrderTypeConstants.SalesOrder,   // "SO" – change if you use a custom type
                CustomerID = parent.CustomerID,
                CustomerLocationID = parent.CustomerLocationID,
                CuryID = parent.CuryID,
                BlanketNbr = parent.OrderNbr,                    // links the two (2020R2+)
                Description = $"Auto-created from blanket {parent.OrderNbr}"
            };
            child = childGraph.Document.Insert(child);

            // 4. Add the single line
            SOLine newLine = new SOLine
            {
                InventoryID = srcLine.InventoryID,
                SubItemID = srcLine.SubItemID,
                UOM = srcLine.UOM,
                OrderQty = srcLine.OpenQty ?? srcLine.OrderQty,
                CuryUnitPrice = srcLine.CuryUnitPrice,
                OrigOrderType = parent.OrderType,                    // traceability
                OrigOrderNbr = parent.OrderNbr,
                OrigLineNbr = srcLine.LineNbr
            };
            childGraph.Transactions.Insert(newLine);

            childGraph.Actions.PressSave();
            return childGraph.Document.Current;
        }
    }
}
