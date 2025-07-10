using System;
using PX.Data;

namespace StandingOrders
{
  [Serializable]
  [PXCacheName("Cycle")]
  public class Cycle : PXBqlTable, IBqlTable
  {
    #region CycleID
    [PXDBIdentity(IsKey = true)]
    public virtual int? CycleID { get; set; }
    public abstract class cycleID : PX.Data.BQL.BqlInt.Field<cycleID> { }
    #endregion

    #region CycleName
    [PXDBString(30, InputMask = "")]
    [PXUIField(DisplayName = "Cycle Name")]
    public virtual string CycleName { get; set; }
    public abstract class cycleName : PX.Data.BQL.BqlString.Field<cycleName> { }
    #endregion
  }
}