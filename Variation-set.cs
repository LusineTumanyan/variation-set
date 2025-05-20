using SellerSolution.BLL.Ebay.Helpers;
using SellerSolution.BLL.Ebay.Mapper;
using SellerSolution.BLL.Helpers;
using SellerSolution.DAL;
using SellerSolution.DTO;
using SellerSolution.DTO.Ebay;
using SellerSolution.DTO.Scraper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using mybaseclass = SellerSolution.BLL.BaseService;

namespace SellerSolution.BLL.Ebay
{
    public static class VariationWorker
    {
        public static bool CreateVariationDetails(ItemModel itemModel, List<string> remainingColorList)
        {
            try
            {
                ConsoleHandler.Message("CreateVariationDetails", "Creating new listing detail");

                List<EbayListingDetail> RespListingDetails = new List<EbayListingDetail>();

                var gen = EbayHelper.GetGenderForPublisher(itemModel);
                if (gen == null)
                {
                    ConsoleHandler.Message("GetGenderForPublisher", $"gender error. item={itemModel.ModelSku}", -1);
                    return false;
                }

                string gender = gen.Item1;
                if (string.IsNullOrWhiteSpace(gender))
                {
                    ConsoleHandler.Message("GetGenderForPublisher", $"error (unknown gender). item={itemModel.ModelSku}", -1);
                    return false;
                }

                var sizeInfo = EbayHelper.GetEbaySizeSpecifier(itemModel.Type, itemModel.Category, gender);
                if (sizeInfo == null)
                {
                    ConsoleHandler.Message("GetEbaySizeSpecifier", $"error (unknown category). category={itemModel.Category} gender={gender} item={itemModel.ModelSku}", -1);
                    return false;
                }

                var uov = new UnitOfWork();
                List<ItemStyle> allIncludedStyles = new List<ItemStyle>();
                List<ItemStyle> stylesIncludedInVariation = new List<ItemStyle>();
                int listedVariationCount = 0;
                bool isFromUpdateVariations = false;
                List<ItemStyle> list = itemModel.Styles;

                do
                {
                    foreach (var style in list.Where(c => !allIncludedStyles.Contains(c)).ToList())
                    {
                        int variationCountForCurrentStyle = style.Stocks.Count;

                        if (listedVariationCount + variationCountForCurrentStyle >= Consts.VariationLimit)
                        {
                            continue;
                        }

                        listedVariationCount += variationCountForCurrentStyle;
                        stylesIncludedInVariation.Add(style);
                        allIncludedStyles.Add(style);
                    }

                    var listDetailThatFit250 = new EbayListingDetail
                    {
                        EbayListingID = null,
                        PushedDate = null,
                        VariationsInListing = listedVariationCount
                    };

                    var styleDet = new List<EbayListingStyleDetail>();

                    foreach (var stl in stylesIncludedInVariation)
                    {
                        var stockDet = new List<EbayListingStockDetail>();
                        var imgDet = new List<EbayListingImageDetail>();

                        foreach (var stk in stl.Stocks)
                        {
                            string validSize = stk.Size;

                            stockDet.Add(new EbayListingStockDetail
                            {
                                ModelCount = stk.OnHand,
                                ModelSize = validSize,
                                ModelWidth = stk.Width
                            });
                        }

                        foreach (var img in stl.ImageUrls)
                        {
                            imgDet.Add(new EbayListingImageDetail
                            {
                                ImageUrl = img
                            });
                        }

                        styleDet.Add(new EbayListingStyleDetail
                        {
                            ModelColorName = stl.ColorName,
                            ModelColorSku = stl.ColorSku,
                            ModelPrice = recalculatedPrice.Value,
                            IsActiveInListing = true,
                            ImageDetails = imgDet,
                            StockDetails = stockDet
                        });
                    }

                    listDetailThatFit250.EbayListingStyleDetails.AddRange(styleDet);
                    RespListingDetails.Add(listDetailThatFit250);
                    listedVariationCount = 0;
                    stylesIncludedInVariation.Clear();

                } while (!allIncludedStyles.Count.Equals(list.Count));

                if (!isFromUpdateVariations)
                {
                    var newModelEbayDetail = new ModelEbayDetails
                    {
                        EbayCategoryID = sizeInfo.EbayCategoryID,
                        EbayCategoryName = sizeInfo.EbayCategoryName,
                        EbaySizeSpecifier = sizeInfo.EbaySizeSpecifier,
                        ModelSku = itemModel.ModelSku,
                        CreateDate = new DateTime(1900, 1, 1)
                    };

                    foreach (var respListDet in RespListingDetails)
                    {
                        newModelEbayDetail.EbayListingDetails.Add(EbayDetailsDalDtoMapper.DtoToDal(respListDet));
                    }

                    uov.GetGenericRepository<ModelEbayDetails>().Insert(newModelEbayDetail);
                    if (!uov.Save()) return false;
                }
                else
                {
                    var existingModelEbayDetail = uov.GetGenericRepository<ModelEbayDetails>()
                        .Get(c => c.ModelSku.Equals(itemModel.ModelSku)).FirstOrDefault();

                    foreach (var ebayListingDetail in existingModelEbayDetail.EbayListingDetails)
                    {
                        foreach (var newlistDet in RespListingDetails.ToList())
                        {
                            if (ebayListingDetail.VariationsInListing + newlistDet.VariationsInListing < Consts.VariationLimit)
                            {
                                var newStylesDal = EbayDetailsDalDtoMapper.DtoToDal(newlistDet).EbayListingStyleDetails;

                                foreach (var s in newStylesDal)
                                {
                                    ebayListingDetail.EbayListingStyleDetails.Add(s);
                                }

                                ebayListingDetail.VariationsInListing += newlistDet.VariationsInListing;
                                RespListingDetails.Remove(newlistDet);
                            }
                        }
                    }

                    foreach (var listDet in RespListingDetails)
                    {
                        existingModelEbayDetail.EbayListingDetails.Add(EbayDetailsDalDtoMapper.DtoToDal(listDet));
                    }

                    uov.GetGenericRepository<ModelEbayDetails>().Update(existingModelEbayDetail);
                    uov.Save();
                }

                return true;
            }
            catch (Exception ex)
            {
                mybaseclass.Logger.Error("CreateVariationDetails", ex);
                return false;
            }
        }

        public static bool UpdateVariationDetails(ItemModel itemModel, ModelEbayDetails existingDetails, ref UnitOfWork uov)
        {
            try
            {
                ConsoleHandler.Message("UpdateVariationDetails", "Updating existing listing details");
                List<string> remaining = new List<string>();

                foreach (var listingDetail in existingDetails.EbayListingDetails)
                {
                    ItemStyle existingStyleInUpdatedItemModel;

                    foreach (var listedStyle in listingDetail.EbayListingStyleDetails.Where(c => c.IsActiveInListing))
                    {
                        existingStyleInUpdatedItemModel = itemModel.Styles
                            .FirstOrDefault(c => c.ColorSku.Equals(listedStyle.ModeColorSku));

                        if (existingStyleInUpdatedItemModel == null)
                        {
                            foreach (var listedStock in listedStyle.EbayListingStockDetails)
                            {
                                listedStock.StockCount = 0;
                                uov.Save();
                            }
                        }
                        else
                        {
                            listedStyle.ModelPrice = recalculatedPrice.Value;
                            uov.Save();

                            var listedStocksForStyle = listedStyle.EbayListingStockDetails.Select(v => v.StockSku).ToList();
                            var addingStocksForStyle = new List<ItemStock>();

                            foreach (var st in existingStyleInUpdatedItemModel.Stocks)
                            {
                                if (!listedStocksForStyle.Contains(st.StockSku))
                                {
                                    addingStocksForStyle.Add(st);
                                }
                            }

                            bool thereAreNewStocks = addingStocksForStyle.Any();
                            bool addedAllNewStocks = false;

                            if (thereAreNewStocks)
                            {
                                if (addingStocksForStyle.Count + listingDetail.VariationsInListing < Consts.VariationLimit)
                                {
                                    listingDetail.VariationsInListing += addingStocksForStyle.Count;
                                    uov.Save();

                                    foreach (var newStockDetail in addingStocksForStyle)
                                    {
                                        string validSize = newStockDetail.Size;

                                        if (itemModel.Type.Equals(Consts.TypeShoes))
                                        {
                                            validSize = EbayHelper.ValidateShoesSize(newStockDetail.Size);
                                        }

                                        if (string.IsNullOrWhiteSpace(validSize))
                                        {
                                            ConsoleHandler.Message("ValidateSize", $"something wrong with size - size={newStockDetail.Size}, stock={newStockDetail.StockSku}", -1);
                                            continue;
                                        }

                                        listedStyle.EbayListingStockDetails.Add(new EbayListingStockDetails()
                                        {
                                            StockCount = newStockDetail.OnHand,
                                            StockSize = validSize,
                                            StockWidth = newStockDetail.Width,
                                            StockSku = newStockDetail.StockSku
                                        });
                                        uov.Save();
                                    }

                                    addedAllNewStocks = true;
                                }
                                else
                                {
                                    listedStyle.IsActiveInListing = false;
                                    uov.Save();

                                    foreach (var listedStock in listedStyle.EbayListingStockDetails)
                                    {
                                        listedStock.StockCount = 0;
                                        uov.Save();
                                    }

                                    remaining.Add(listedStyle.ModeColorSku);
                                }
                            }

                            if (!thereAreNewStocks || (thereAreNewStocks && addedAllNewStocks))
                            {
                                foreach (var listedStock in listedStyle.EbayListingStockDetails)
                                {
                                    var existStockInUpdatedItemModel = existingStyleInUpdatedItemModel.Stocks
                                        .FirstOrDefault(c => c.StockSku.Equals(listedStock.StockSku));

                                    if (existStockInUpdatedItemModel == null)
                                    {
                                        listedStock.StockCount = 0;
                                        uov.Save();
                                    }
                                    else
                                    {
                                        string validSize = existStockInUpdatedItemModel.Size;

                                        if (itemModel.Type.Equals(Consts.TypeShoes))
                                        {
                                            validSize = EbayHelper.ValidateShoesSize(existStockInUpdatedItemModel.Size);
                                        }

                                        if (string.IsNullOrWhiteSpace(validSize))
                                        {
                                            ConsoleHandler.Message("ValidateSize", $"something wrong with size - size={existStockInUpdatedItemModel.Size}, stock={existStockInUpdatedItemModel.StockSku}", -1);
                                            continue;
                                        }

                                        listedStock.StockCount = existStockInUpdatedItemModel.OnHand;
                                        uov.Save();
                                        listedStock.StockSize = validSize;
                                        uov.Save();
                                        listedStock.StockWidth = existStockInUpdatedItemModel.Width;
                                        uov.Save();
                                        listedStock.StockUPC = existStockInUpdatedItemModel.UPC;
                                        uov.Save();
                                    }
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                mybaseclass.Logger.Error("UpdateVariationDetails", ex);
                return false;
            }
        }
    }
}
