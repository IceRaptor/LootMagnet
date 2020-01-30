using BattleTech;
using BattleTech.UI;
using Harmony;
using Localize;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BattleTech.Designed;
using TMPro;
using static LootMagnet.LootMagnet;

namespace LootMagnet {

    public class UIHelper {
        // does not account for matching to assemble, too complex (ref CustomSalvage instead)
        public static string AppendExistingPartialCount(string localItemName, SalvageDef sDef)
        {
            if (!localItemName.Contains("Partial Mech Salvage"))
                return localItemName;
            var sim = UnityGameInstance.BattleTechGame.Simulation;
            var mechName = localItemName.Split(' ')[0];
            var matchingChassis = sim.GetAllInventoryMechDefs()
                .Where(x => ParseName(x.Description.Name) == mechName.ToLower()).ToList();
            Mod.Log.Debug("Matching chassis:");
            matchingChassis.Do(x => Mod.Log.Debug($"\t{x.Description.Name}"));
            var count = 0;
            foreach (var chassis in matchingChassis)
            {
                Mod.Log.Debug($"{chassis.Description.Name}, parsed {ParseName(chassis.Description.Name)}");
                var id = chassis.Description.Id.Replace("chassisdef", "mechdef");
                count += sim.GetItemCount(id, "MECHPART", SimGameState.ItemCountType.UNDAMAGED_ONLY);
            }

            return $"{localItemName} (Have {count})";
        }
        
        // match RT Names
        // change __/Base_3061.mechdef_clint_CLNT-2-2R.Name/__ into clint
        public static string ParseName(string input)
        {
            var match = Regex.Match(input.ToLower(), @"def_(\w+)_");
            return match.Groups[1].Value == "" ? input : match.Groups[1].Value;
        }
        
        public static void ShowHoldbackDialog(Contract contract, AAR_SalvageScreen salvageScreen) {

            List<string> heldbackItemsDesc = new List<string>();
            foreach (SalvageDef sDef in ModState.HeldbackParts) {
                string localItemName = new Text(sDef.Description.Name).ToString();
                string localItemAndQuantity = 
                    new Text(
                        Mod.Config.DialogText[ModConfig.DT_ITEM_AND_QUANTITY], new object[] { localItemName, sDef.Count }
                        ).ToString();
                localItemAndQuantity = AppendExistingPartialCount(localItemAndQuantity, sDef);
                heldbackItemsDesc.Add(localItemAndQuantity);
            }
            string heldbackDescs = " -" + string.Join("\n -", heldbackItemsDesc.ToArray());

            List<string> compItemsDesc = new List<string>();
            foreach (SalvageDef sDef in ModState.CompensationParts) {
                string localItemName = new Text(sDef.Description.Name).ToString();
                string localItemAndQuantity =
                    new Text(
                        Mod.Config.DialogText[ModConfig.DT_ITEM_AND_QUANTITY], new object[] { localItemName, sDef.Count }
                        ).ToString();
                compItemsDesc.Add(localItemAndQuantity);
            }
            string compDescs = " -" + string.Join("\n -", compItemsDesc.ToArray());

            int acceptRepMod = LootMagnet.Random.Next(Mod.Config.Holdback.ReputationRange[0], Mod.Config.Holdback.ReputationRange[1]);
            int refuseRepMod = LootMagnet.Random.Next(Mod.Config.Holdback.ReputationRange[0], Mod.Config.Holdback.ReputationRange[1]);
            int disputeRepMod = LootMagnet.Random.Next(Mod.Config.Holdback.ReputationRange[0], Mod.Config.Holdback.ReputationRange[1]);
            Mod.Log.Debug($"Reputation modifiers - accept:{acceptRepMod} refuse:{refuseRepMod} dispute:{disputeRepMod}");

            Dispute dispute = new Dispute(contract.InitialContractValue, contract.Name);
            void acceptAction() { AcceptAction(salvageScreen, acceptRepMod); }
            void refuseAction() { RefuseAction(salvageScreen, refuseRepMod); }
            void disputeAction() { DisputeAction(contract, salvageScreen, dispute); }

            string localDialogTitle = new Text(Mod.Config.DialogText[ModConfig.DT_DISPUTE_TITLE]).ToString();
            string localDialogText = new Text(
                Mod.Config.DialogText[ModConfig.DT_DISPUTE_TEXT], new object[] {
                    ModState.Employer, heldbackDescs, compDescs, refuseRepMod, acceptRepMod,
                    SimGameState.GetCBillString(dispute.MRBFees), dispute.SuccessChance,
                    Mod.Config.Holdback.DisputePicks[0], Mod.Config.Holdback.DisputePicks[1],
                    (100 - dispute.SuccessChance),
                    Mod.Config.Holdback.DisputePicks[0], Mod.Config.Holdback.DisputePicks[1],
                }
                ).ToString();
            string localButtonRefuse = new Text(Mod.Config.DialogText[ModConfig.DT_BUTTON_REFUSE]).ToString();
            string localButtonAccept = new Text(Mod.Config.DialogText[ModConfig.DT_BUTTON_ACCEPT]).ToString();
            string localButtonDispute = new Text(Mod.Config.DialogText[ModConfig.DT_BUTTON_DISPUTE]).ToString();
            GenericPopup gp = GenericPopupBuilder.Create(localDialogTitle, localDialogText)
                .AddButton(localButtonRefuse, refuseAction, true, null)
                .AddButton(localButtonAccept, acceptAction, true, null)
                .AddButton(localButtonDispute, disputeAction, true, null)
                .Render();

            TextMeshProUGUI contentText = (TextMeshProUGUI)Traverse.Create(gp).Field("_contentText").GetValue();
            contentText.alignment = TextAlignmentOptions.Left;
        }
        
        public static void AcceptAction(AAR_SalvageScreen salvageScreen, int reputationModifier) {

            SimGameState sgs = UnityGameInstance.BattleTechGame.Simulation;
            int repBefore = sgs.GetRawReputation(ModState.Employer);
            sgs.AddReputation(ModState.Employer, reputationModifier, false);
            ModState.EmployerRepRaw = sgs.GetRawReputation(ModState.Employer);
            Mod.Log.Info($"Player accepted holdback. {ModState.Employer} reputation {repBefore} + {reputationModifier} modifier = {ModState.EmployerRepRaw}.");

            // Remove the disputed items
            Mod.Log.Debug("  -- Removing disputed items.");
            foreach (SalvageDef sDef in ModState.HeldbackParts) {
                Helper.RemoveSalvage(sDef);
            }

            // Update quantities of compensation parts
            Mod.Log.Debug("  -- Updating quantities on compensation parts.");
            foreach (SalvageDef compSDef in ModState.CompensationParts) {
                Mod.Log.Debug($"   compensation salvageDef:{compSDef.Description.Name} with quantity:{compSDef.Count}");
                foreach (SalvageDef sDef in ModState.PotentialSalvage) {
                    Mod.Log.Debug($"   salvageDef:{sDef.Description.Name} with quantity:{sDef.Count}");

                    if (compSDef.RewardID == sDef.RewardID) {
                        Mod.Log.Info($"   Matched compensation target, updating quantity to: {compSDef.Count + sDef.Count}");
                        sDef.Count = sDef.Count + compSDef.Count;
                        break;
                    }
                }
            }

            // Roll up any remaining salvage and widget-tize it
            List<SalvageDef> rolledUpSalvage = Helper.RollupSalvage(ModState.PotentialSalvage);
            Helper.CalculateAndAddAvailableSalvage(salvageScreen, rolledUpSalvage);

            ModState.Reset();
        }

        public static void RefuseAction(AAR_SalvageScreen salvageScreen, int reputationModifier) {

            SimGameState sgs = UnityGameInstance.BattleTechGame.Simulation;
            int repBefore = sgs.GetRawReputation(ModState.Employer);
            sgs.AddReputation(ModState.Employer, reputationModifier, false);
            ModState.EmployerRepRaw = sgs.GetRawReputation(ModState.Employer);
            Mod.Log.Info($"Player refused holdback. {ModState.Employer} reputation {repBefore} + {reputationModifier} modifier = {ModState.EmployerRepRaw}.");

            // Roll up any remaining salvage and widget-tize it
            List<SalvageDef> rolledUpSalvage = Helper.RollupSalvage(ModState.PotentialSalvage);
            Helper.CalculateAndAddAvailableSalvage(salvageScreen, rolledUpSalvage);

            ModState.Reset();
        }

        public static void DisputeAction(Contract contract, AAR_SalvageScreen salvageScreen, Dispute dispute) {
            Mod.Log.Info($"Player disputed holdback.");

            SimGameState sgs = UnityGameInstance.BattleTechGame.Simulation;
            Mod.Log.Info($"  Dispute legal fees:{dispute.MRBFees}");
            sgs.AddFunds(dispute.MRBFees, $"MRB Legal Fees re: {contract.Name}", false);

            Dispute.Outcome outcome = dispute.GetOutcome();
            if (outcome == Dispute.Outcome.SUCCESS) {
                Mod.Log.Info($"DISPUTE SUCCESS: Player keeps disputed salvage and gains {dispute.Picks} items from compensation pool.");

                // Update quantities of compensation parts
                Mod.Log.Debug("  -- Updating quantities on compensation parts.");
                List<string> compItemsDesc = new List<string>();
                int loopCount = 0;
                foreach (SalvageDef compSDef in ModState.CompensationParts) {
                    if (loopCount < dispute.Picks) { loopCount++; } 
                    else { break; }

                    Mod.Log.Debug($"   compensation salvageDef:{compSDef.Description.Name} with quantity:{compSDef.Count}");
                    foreach (SalvageDef sDef in ModState.PotentialSalvage) {
                        Mod.Log.Debug($"   salvageDef:{sDef.Description.Name} with quantity:{sDef.Count}");

                        if (compSDef.RewardID == sDef.RewardID) {
                            Mod.Log.Debug($"   Matched compensation target, updating quantity to: {compSDef.Count + sDef.Count}");

                            string localItemName = new Text(compSDef.Description.Name).ToString();
                            string localItemAndQuantity =
                                new Text(
                                    Mod.Config.DialogText[ModConfig.DT_ITEM_AND_QUANTITY], new object[] { localItemName, compSDef.Count }
                                    ).ToString();
                            compItemsDesc.Add(localItemAndQuantity);
                            sDef.Count = sDef.Count + compSDef.Count;
                            break;
                        }
                    }
                }
                string compDescs = " -" + string.Join("\n -", compItemsDesc.ToArray());

                // Display the confirmation screen
                string localDialogTitle = new Text(Mod.Config.DialogText[ModConfig.DT_SUCCESS_TITLE]).ToString();
                string localDialogText = new Text(
                    Mod.Config.DialogText[ModConfig.DT_SUCCESS_TEXT], new object[] { compDescs }
                ).ToString();
                string localButtonOk = new Text(Mod.Config.DialogText[ModConfig.DT_BUTTON_OK]).ToString();
                GenericPopupBuilder.Create(localDialogTitle, localDialogText)
                    .AddButton("OK")
                    .Render();

            } else {
                Mod.Log.Info($"DISPUTE FAILURE: Player loses disputed items, and {dispute.Picks} items from the salvage pool.");

                // Remove the disputed items
                Mod.Log.Debug("  -- Removing disputed items.");
                foreach (SalvageDef sDef in ModState.HeldbackParts) {
                    Helper.RemoveSalvage(sDef);
                }

                // Update quantities of compensation parts
                Mod.Log.Debug("  -- Determining dispute failure picks.");
                List<SalvageDef> disputePicks = new List<SalvageDef>();
                List<SalvageDef> components = ModState.PotentialSalvage.Where(sd => sd.Type == SalvageDef.SalvageType.COMPONENT).ToList();
                components.Sort(new Helper.SalvageDefByCostDescendingComparer());
                int loopCount = 0;
                foreach (SalvageDef compDef in components) {
                    if (loopCount < dispute.Picks) { loopCount++; }
                    else { break; }

                    Mod.Log.Debug($"   dispute fail salvageDef:{compDef.Description.Name} with quantity:{compDef.Count}");
                    disputePicks.Add(compDef);
                    ModState.PotentialSalvage.Remove(compDef);
                }

                List<string> heldbackItemsDesc = new List<string>();
                foreach (SalvageDef sDef in ModState.HeldbackParts) {
                    heldbackItemsDesc.Add($"{sDef.Description.Name} [QTY:{sDef.Count}]");
                }
                string heldbackDescs = " -" + string.Join("\n -", heldbackItemsDesc.ToArray());

                List<string> disputeDesc = new List<string>();
                foreach (SalvageDef sDef in disputePicks) {
                    disputeDesc.Add($"{sDef.Description.Name} [QTY:{sDef.Count}]");
                }
                string disputeDescs = " -" + string.Join("\n -", disputeDesc.ToArray());

                // Display the configmration screen
                string localDialogTitle = new Text(Mod.Config.DialogText[ModConfig.DT_FAILED_TITLE]).ToString();
                string localDialogText = new Text(
                    Mod.Config.DialogText[ModConfig.DT_FAILED_TEXT], new object[] {
                        ModState.Employer, sgs.CompanyName, heldbackDescs, disputeDescs
                    }).ToString();
                string localButtonOk = new Text(Mod.Config.DialogText[ModConfig.DT_BUTTON_OK]).ToString();
                GenericPopupBuilder.Create(localDialogTitle, localDialogText)
                    .AddButton(localButtonOk)
                    .Render();
            }

            // Roll up any remaining salvage and widget-tize it
            List<SalvageDef> rolledUpSalvage = Helper.RollupSalvage(ModState.PotentialSalvage);
            Helper.CalculateAndAddAvailableSalvage(salvageScreen, rolledUpSalvage);

            ModState.Reset();
        }

        public class SalvageDefByCostDescendingComparer : IComparer<SalvageDef> {
            public int Compare(SalvageDef x, SalvageDef y) {
                if (object.ReferenceEquals(x, y))
                    return 0;
                if (x == null || x.Description == null)
                    return -1;
                if (y == null || y.Description == null)
                    return 1;

                return -1 * x.Description.Cost.CompareTo(y.Description.Cost);
            }
        }

        // This always returns a quantity of 1!
        public static SalvageDef CloneToXName(SalvageDef salvageDef, int quantity, int count) {

            string localItemName = new Text(salvageDef.Description.Name).ToString();
            string localItemAndQuantity = new Text(
                    Mod.Config.DialogText[ModConfig.DT_ITEM_AND_QUANTITY], new object[] { localItemName, quantity }
                ).ToString();
            DescriptionDef newDescDef = new DescriptionDef(
                salvageDef.Description.Id,
                salvageDef.Description.Name,
                salvageDef.Description.Details,
                salvageDef.Description.Icon,
                salvageDef.Description.Cost,
                salvageDef.Description.Rarity,
                salvageDef.Description.Purchasable,
                salvageDef.Description.Manufacturer,
                salvageDef.Description.Model,
                localItemAndQuantity
            );

            SalvageDef newDef = new SalvageDef(salvageDef) {
                Description = newDescDef,
                RewardID = $"{salvageDef.RewardID}_c{count}_qty{quantity}",
                Count = 1
            };

            return newDef;
        }

        public static int FactionCfgIdx() {
            int cfgIdx = 0;
            switch (ModState.EmployerRep) {
                case SimGameReputation.LOATHED:
                    cfgIdx = 0;
                    break;
                case SimGameReputation.HATED:
                    cfgIdx = 1;
                    break;
                case SimGameReputation.DISLIKED:
                    cfgIdx = 2;
                    break;
                case SimGameReputation.INDIFFERENT:
                    cfgIdx = 3;
                    break;
                case SimGameReputation.LIKED:
                    cfgIdx = 4;
                    break;
                case SimGameReputation.FRIENDLY:
                    cfgIdx = 5;
                    break;
                case SimGameReputation.HONORED:
                default:
                    cfgIdx = 6;
                    break;
            }

            // Check for allied
            if (ModState.IsEmployerAlly) {
                cfgIdx = 7;
            }

            return cfgIdx;
        }

        public static int MRBCfgIdx() {
            if (ModState.MRBRating <= 0) {
                return 0;
            } else if (ModState.MRBRating >= 5) {
                return 5;
            } else {
                return ModState.MRBRating;
            }
        }
    }
}
