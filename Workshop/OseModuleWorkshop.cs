﻿namespace Workshop
{
    using System;
    using System.Collections;
    using System.Linq;
    using System.Collections.Generic;

    using W_KIS;

    using UnityEngine;

    using Recipes;
    using System.Reflection;
    using System.Text;

    using ClickThroughFix;

    public class OseModuleWorkshop : PartModule
    {
        private const double kBackgroundProcessInterval = 3600f;

        private static Version modVersion = Assembly.GetExecutingAssembly().GetName().Version;

        private WorkshopItem[] _availableItems;
        private FilterResult _filteredItems;

        private Blueprint _processedBlueprint;
        private WorkshopItem _processedItem;

        private float _maxVolume;

        private readonly WorkshopQueue _queue;

        // Animations
        private AnimationState _heatAnimation;
        private AnimationState _workAnimation;

        // GUI Properties
        private FilterBase[] _filters;
        private FilterSearch _searchFilter = new FilterSearch();
        private Texture[] _filterTextures;

        private int _activeFilterId;
        private int _selectedFilterId;

        private int _activePage;
        private int _selectedPage;

        private string _oldSsearchText = "";

        private Rect _windowPos = new Rect(50, 50, 640, 680);
        private bool _showGui;

        private bool _confirmDelete;

        [KSPField(isPersistant = true)]
        public bool manufacturingPaused;

        [KSPField(isPersistant = true)]
        public float progress;

        [KSPField(isPersistant = true)]
        public double lastUpdateTime;

        [KSPField]
        public bool Animate = false;

        [KSPField]
        public float ProductivityFactor = 0.1f;

        [KSPField]
        public string UpkeepResource = "ElectricCharge";

        [KSPField]
        public float UpkeepAmount = 1.0f;

        [KSPField]
        public int MinimumCrew = 2;

        [KSPField]
        public bool UseSpecializationBonus = true;

        [KSPField]
        public string ExperienceEffect = "RepairSkill";

        [KSPField]
        public float SpecialistEfficiencyFactor = 0.02f;

        [KSPField(guiName = "Workshop Status", guiActive = true, guiActiveEditor = false)]
        public string Status = "Online";

        [KSPField]
        public string WorkAnimationName = "work";

        [KSPField(isPersistant = true)]
        public string KACAlarmID = string.Empty;
        KACWrapper.KACAPI.KACAlarm kacAlarm = null;
        int kacAlarmIndex = -1;

        protected float adjustedProductivity = 1.0f;

        [KSPField(isPersistant = true)]
        double maxGeeForce = 1;



        private readonly Texture2D _pauseTexture;
        private readonly Texture2D _playTexture;
        private readonly Texture2D _binTexture;

        protected Recipe workshopRecipe;

 

        [KSPEvent(guiName = "Open OSE Workbench", guiActive = true, guiActiveEditor = false)]
        public void ContextMenuOpenWorkbench()
        {
            if (_showGui)
            {
                foreach (var item in _filteredItems.Items)
                {
                    item.DisableIcon();
                }
                foreach (var item in _queue)
                {
                    item.DisableIcon();
                }
                if (_processedItem != null)
                {
                    _processedItem.DisableIcon();
                }
                _showGui = false;
            }
            else
            {
                if (!WorkshopUtils.PreLaunch())
                {
                    LoadAvailableParts();
                    _showGui = true;
                }
                else
                {
                    ScreenMessages.PostScreenMessage("3D Printer is in travel mode, unable to print at this time", 5, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }


        public OseModuleWorkshop()
        {
            _queue = new WorkshopQueue();
            _pauseTexture = WorkshopUtils.LoadTexture("Workshop/Assets/Icons/icon_pause");
            _playTexture = WorkshopUtils.LoadTexture("Workshop/Assets/Icons/icon_play");
            _binTexture = WorkshopUtils.LoadTexture("Workshop/Assets/Icons/icon_bin");
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder("<color=#8dffec>KIS Part Printing Workshop</color>");

            sb.Append($"\nMinimum Crew: {MinimumCrew}");
            sb.Append($"\nBase productivity factor: {ProductivityFactor:P0}");
            sb.Append($"\nUse specialist bonus: ");
            sb.Append(RUIutils.GetYesNoUIString(UseSpecializationBonus));
            if (UseSpecializationBonus)
            {
                sb.Append($"\nSpecialist skill: {ExperienceEffect}");
                sb.Append($"\nSpecialist bonus: {SpecialistEfficiencyFactor:P0} per level");
            }
            return sb.ToString();
        }

        WorkshopAnimateGeneric wag;
        WorkshopDamageController wdc;

        public override void OnStart(StartState state)
        {
            //Init the KAC Wrapper. KAC Wrapper courtey of TriggerAu
            KACWrapper.InitKACWrapper();
            if (KACWrapper.APIReady)
            {
                KACWrapper.KAC.onAlarmStateChanged += KAC_onAlarmStateChanged;
            }

            if (HighLogic.LoadedSceneIsFlight && WorkshopSettings.IsKISAvailable)
            {
                WorkshopUtils.Log("KIS is available - Initialize Workshop");
                SetupAnimations();
                LoadMaxVolume();
                LoadFilters();
                if (lastUpdateTime == 0)
                    lastUpdateTime = Planetarium.GetUniversalTime();
                GameEvents.onVesselChange.Add(OnVesselChange);
            }
    
            foreach (PartModule p in this.part.Modules)
            {
                if (p.moduleName == "WorkshopAnimateGeneric")
                {
                    wag = p as WorkshopAnimateGeneric;
                }
                if (p.moduleName == "WorkshopDamageController")
                {
                    wdc = p as WorkshopDamageController;
                }
            }
            if (wag != null && wag.packed)
                Status = "Packed";

            //this.part.GetComponent<WorkshopAnimateGeneric>()

            base.OnStart(state);
        }

        void KAC_onAlarmStateChanged(KACWrapper.KACAPI.AlarmStateChangedEventArgs args)
        {

        }

        void setKACAlarm(double totalPrintTime)
        {
            if (!KACWrapper.AssemblyExists)
                return;
            if (!KACWrapper.APIReady)
                return;

            //Delete the alarm if it exists
            if (!string.IsNullOrEmpty(KACAlarmID))
                KACWrapper.KAC.DeleteAlarm(KACAlarmID);

            //Get the start time
            double startTime = Planetarium.GetUniversalTime();

            //Now set the alarm
            double buildTimeSeconds = Planetarium.GetUniversalTime() + totalPrintTime;
            KACAlarmID = KACWrapper.KAC.CreateAlarm(KACWrapper.KACAPI.AlarmTypeEnum.Raw, "Print job completed", buildTimeSeconds);
            kacAlarm = getKACAlarm();
            if (kacAlarm != null)
            {
                kacAlarm.AlarmMargin = 5.0f;
                kacAlarm.Notes = this.part.vessel.vesselName + " completed print job.";
                kacAlarm.VesselID = FlightGlobals.ActiveVessel.id.ToString();
                KACWrapper.KAC.Alarms[kacAlarmIndex] = kacAlarm;
            }
            else
                Log.Info("setKACAlarm, alarm not set");
        }

        KACWrapper.KACAPI.KACAlarm getKACAlarm()
        {
            kacAlarmIndex = -1;
            if (KACWrapper.AssemblyExists && KACWrapper.APIReady && !string.IsNullOrEmpty(KACAlarmID))
            {
                int totalAlarms = KACWrapper.KAC.Alarms.Count;
                for (int index = 0; index < totalAlarms; index++)
                {
                    if (KACWrapper.KAC.Alarms[index].ID == KACAlarmID)
                    {
                        kacAlarmIndex = index;
                        return KACWrapper.KAC.Alarms[index];
                    }
                }
            }

            return null;
        }

        void updateKACAlarm()
        {
            if (!WorkshopOptions.EnableKACIntegration)
                return;
            if (!KACWrapper.AssemblyExists)
                return;
            if (!KACWrapper.APIReady)
                return;

            if (_queue.Count == 0 && _processedBlueprint == null)
            {
                deleteKACAlarm();
                return;
            }

            //Calculate total print time.
            double totalPrintTime = 0;

            int totalItems = _queue.Count;
            for (int index = 0; index < totalItems; index++)
            {
                totalPrintTime += _queue[index].PartBlueprint.GetBuildTime(WorkshopUtils.ProductivityType.printer, adjustedProductivity);
            }
            if (_processedBlueprint != null)
                totalPrintTime += _processedBlueprint.GetBuildTime(WorkshopUtils.ProductivityType.printer, adjustedProductivity);

            //Create the alarm if needed.
            if (string.IsNullOrEmpty(KACAlarmID))
            {
                setKACAlarm(totalPrintTime);
            }
            else
            {

                //Find the alarm if needed and then update it
                if (kacAlarm == null)
                {
                    for (int index = KACWrapper.KAC.Alarms.Count - 1; index >= 0; index--)
                    {
                        kacAlarm = KACWrapper.KAC.Alarms[index];
                        if (KACWrapper.KAC.Alarms[index].ID == KACAlarmID)
                        {
                            kacAlarm.AlarmTime = Planetarium.GetUniversalTime() + totalPrintTime;
                            KACWrapper.KAC.Alarms[index] = kacAlarm;
                            kacAlarmIndex = index;
                            return;
                        }
                    }
                }

                //Update the alarm
                else
                {

                    kacAlarm.AlarmTime = Planetarium.GetUniversalTime() + totalPrintTime;
                    if (kacAlarmIndex < 0 || kacAlarmIndex >= KACWrapper.KAC.Alarms.Count || KACWrapper.KAC.Alarms[kacAlarmIndex].ID != kacAlarm.ID)
                    {

                        kacAlarmIndex = -1;
                        for (int index = KACWrapper.KAC.Alarms.Count - 1; index >= 0; index--)
                        {
                            kacAlarm = KACWrapper.KAC.Alarms[index];
                            if (KACWrapper.KAC.Alarms[index].ID == kacAlarm.ID)
                            {
                                kacAlarmIndex = index;
                                break;
                            }
                        }
                    }

                    if (kacAlarmIndex >= 0)    
                        KACWrapper.KAC.Alarms[kacAlarmIndex] = kacAlarm;
                }
            }
        }

        void deleteKACAlarm()
        {
            if (!string.IsNullOrEmpty(KACAlarmID))
            {
                if (KACWrapper.AssemblyExists && KACWrapper.APIReady)
                {
                    int totalAlarms = KACWrapper.KAC.Alarms.Count;
                    for (int index = 0; index < totalAlarms; index++)
                    {
                        if (KACWrapper.KAC.Alarms[index].ID == KACAlarmID)
                        {
                            KACWrapper.KAC.DeleteAlarm(KACAlarmID);
                            KACAlarmID = string.Empty;
                            kacAlarm = null;
                            return;
                        }
                    }
                }
                KACAlarmID = string.Empty;
                kacAlarm = null;

                Log.Info("Alarm not deleted");
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                LoadModuleState(node);
            }
            base.OnLoad(node);
        }

        private void SetupAnimations()
        {
            if (Animate)
            {
                foreach (var animator in part.FindModelAnimators("workshop_emissive"))
                {
                    _heatAnimation = animator["workshop_emissive"];
                    if (_heatAnimation != null)
                    {
                        _heatAnimation.speed = 0;
                        _heatAnimation.enabled = true;
                        _heatAnimation.wrapMode = WrapMode.ClampForever;
                        animator.Blend("workshop_emissive");
                        break;
                    }
                    WorkshopUtils.LogError("Unable to load workshop_emissive animation");
                }
                foreach (var animator in part.FindModelAnimators(WorkAnimationName))
                {
                    _workAnimation = animator[WorkAnimationName];
                    if (_workAnimation != null)
                    {
                        _workAnimation.speed = 0;
                        _workAnimation.enabled = true;
                        _workAnimation.wrapMode = WrapMode.ClampForever;
                        animator.Blend(WorkAnimationName);
                        break;
                    }
                    WorkshopUtils.LogError("Unable to load work animation");
                }
            }
        }

        private void LoadFilters()
        {
            var filters = new List<FilterBase>();
            var filterTextures = new List<Texture>();
            if (this.part.partInfo.partConfig == null)
                return;
            ConfigNode[] nodes = this.part.partInfo.partConfig.GetNodes("MODULE");
            ConfigNode node = null;
            ConfigNode workshopNode = null;
            PartCategories category;

            //Get the nodes we're interested in
            for (int index = 0; index < nodes.Length; index++)
            {
                node = nodes[index];
                if (node.HasValue("name"))
                {
                    moduleName = node.GetValue("name");
                    if (moduleName == this.ClassName)
                    {
                        workshopNode = node;
                        break;
                    }
                }
            }
            if (workshopNode == null)
                return;

            //Each category represets one of the tab buttons from the KSP editor.
            //Mods have the ability to specify which categories that the workshop can produce.
            //If there are no CATEGORY nodes specified then the defaults are used instead.
            nodes = workshopNode.GetNodes("CATEGORY");

            //If we have no category nodes then just load the defaults.
            if (nodes.Length == 0)
            {
                //Pods
                filters.Add(new FilterCategory(PartCategories.Pods));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/RDicon_commandmodules"));

                //FuelTank
                filters.Add(new FilterCategory(PartCategories.FuelTank));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/RDicon_fuelSystems-advanced"));

                //Engine
                filters.Add(new FilterCategory(PartCategories.Engine));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/RDicon_propulsionSystems"));

                //Control
                filters.Add(new FilterCategory(PartCategories.Control));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_largecontrol"));

                //Structural
                filters.Add(new FilterCategory(PartCategories.Structural));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_generalconstruction"));

                //Aero
                filters.Add(new FilterCategory(PartCategories.Aero));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_advaerodynamics"));

                //Utility
                filters.Add(new FilterCategory(PartCategories.Utility));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_generic"));

                //Electrical
                filters.Add(new FilterCategory(PartCategories.Electrical));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_electrics"));

                //Ground
                filters.Add(new FilterCategory(PartCategories.Ground));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_advancedmotors"));

                //Payload
                filters.Add(new FilterCategory(PartCategories.Payload));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_composites"));

                //Communications
                filters.Add(new FilterCategory(PartCategories.Communication));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_advunmanned"));

                //Coupling
                filters.Add(new FilterCategory(PartCategories.Coupling));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/cs_size3"));

                //Thermal
                filters.Add(new FilterCategory(PartCategories.Thermal));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/fuels_monopropellant"));

                //Science
                filters.Add(new FilterCategory(PartCategories.Science));
                filterTextures.Add(WorkshopUtils.LoadTexture("Squad/PartList/SimpleIcons/R&D_node_icon_advsciencetech"));

                if (HighLogic.CurrentGame.Parameters.CustomParams<Workshop_MiscSettings>().showMiscCategory)
                {
                    //Misc (catchall for parts not in other categories)
                    filters.Add(new FilterCategory(PartCategories.none));
                    filterTextures.Add(WorkshopUtils.LoadTexture("Workshop/Assets/Icons/Misc"));
                }
            }

            else //Load all the specified CATEGORY nodes.
            {
                //Load the categories
                for (int index = 0; index < nodes.Length; index++)
                {
                    node = nodes[index];
                    if (!node.HasValue("name") && !node.HasValue("iconPath"))
                        continue;

                    try
                    {
                        category = (PartCategories)Enum.Parse(typeof(PartCategories), node.GetValue("name"));
                        filters.Add(new FilterCategory(category));
                        filterTextures.Add(WorkshopUtils.LoadTexture(node.GetValue("iconPath")));
                    }
                    catch (Exception ex)
                    {
                        WorkshopUtils.LogError("Error during LoadFilters: " + ex.ToString());
                        continue;
                    }
                }
            }

            _filters = filters.ToArray();
            _filterTextures = filterTextures.ToArray();
            _searchFilter = new FilterSearch();
        }

        private void LoadModuleState(ConfigNode node)
        {
            if (node.HasValue("progress"))
                float.TryParse(node.GetValue("progress"), out progress);
            if (node.HasValue("lastUpdateTime"))
                double.TryParse(node.GetValue("lastUpdateTime"), out lastUpdateTime);
            if (node.HasValue("KACAlarmID"))
                KACAlarmID = node.GetValue("KACAlarmID");


            foreach (ConfigNode cn in node.nodes)
            {
                if (cn.name == "ProcessedItem")
                {
                    _processedItem = new WorkshopItem();
                    _processedItem.Load(cn);
                }
                if (cn.name == "ProcessedBlueprint")
                {
                    _processedBlueprint = new Blueprint();
                    _processedBlueprint.Load(cn);
                }
                if (cn.name == "Queue")
                {
                    _queue.Load(cn);
                }
            }
        }

        private void LoadAvailableParts()
        {
            WorkshopUtils.LogVerbose(PartLoader.LoadedPartsList.Count + " loaded parts");
            WorkshopUtils.LogVerbose(PartLoader.LoadedPartsList.Count(WorkshopUtils.PartResearched) + " unlocked parts");

            var items = new List<WorkshopItem>();
            foreach (var loadedPart in PartLoader.LoadedPartsList.Where(p => p.name != "flag" && !p.name.StartsWith("kerbalEVA")))
            {
                try
                {
                    if (IsValid(loadedPart))
                    {
                        items.Add(new WorkshopItem(loadedPart));
                    }
                }
                catch (Exception ex)
                {
                    WorkshopUtils.LogError("Part " + loadedPart.name + " could not be added to available parts list", ex);
                }
            }
            _availableItems = items.OrderBy(i => i.Part.title).ToArray();
            if (string.IsNullOrEmpty(_searchFilter.FilterText))
                _filteredItems = _filters[_activeFilterId].Filter(_availableItems, 0);
            else
                _filteredItems = _searchFilter.Filter(_availableItems, 0);
        }

        private bool IsValid(AvailablePart loadedPart)
        {
            return WorkshopUtils.PartResearched(loadedPart) && WorkshopUtils.GetPackedPartVolume(loadedPart) <= _maxVolume && !WorkshopBlacklistItemsDatabase.Blacklist.Contains(loadedPart.name);
        }

        private void LoadMaxVolume()
        {
            try
            {
                var inventories = KISWrapper.GetInventories(vessel);
                if (inventories.Count == 0)
                {
                    WorkshopUtils.LogError("No Inventories found on this vessel!");

                }
                else
                {

                    WorkshopUtils.Log(inventories.Count + " inventories found on this vessel!");
                    _maxVolume = inventories.Max(i => i.maxVolume);
                }
            }
            catch (Exception ex)
            {
                WorkshopUtils.LogError("Error while determing maximum volume of available inventories!", ex);
            }
            WorkshopUtils.Log($"Max volume is: {_maxVolume} liters");
        }

        public override void OnSave(ConfigNode node)
        {
            node.SetValue("progress", progress, true);
            node.SetValue("lastUpdateTime", lastUpdateTime, true);
            if (!string.IsNullOrEmpty("KACAlarmID"))
                node.SetValue("KACAlarmID", KACAlarmID, true);

            if (_processedItem != null)
            {
                var itemNode = node.AddNode("ProcessedItem");
                _processedItem.Save(itemNode);

                var blueprintNode = node.AddNode("ProcessedBlueprint");
                _processedBlueprint.Save(blueprintNode);
            }

            var queueNode = node.AddNode("Queue");
            _queue.Save(queueNode);

            base.OnSave(node);
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            maxGeeForce = Math.Max(maxGeeForce, FlightGlobals.ActiveVessel.geeForce);

            if (_showGui)
                Events["ContextMenuOpenWorkbench"].guiName = "Close OSE Workbench";
            else
                Events["ContextMenuOpenWorkbench"].guiName = "Open OSE Workbench";
 

            if (HighLogic.CurrentGame.Parameters.CustomParams<Workshop_Settings>().requireUnpacking || WorkshopUtils.PreLaunch())
            {
                if (wag != null)
                {
                    if (!wag.packed && !wag.Packing)
                        Events["ContextMenuOpenWorkbench"].guiActive = true; // (_processedItem == null);
                    else
                        Events["ContextMenuOpenWorkbench"].guiActive = false;
                }
            }
            
            if (wag != null)
                wag.Busy = (_processedItem != null);

            if (wag != null && wag.packed)
                Status = "Packed";
            else
                Status = "Online";
            
            try
            {
                UpdateProductivity();

                ApplyFilter();
                if (HighLogic.CurrentGame.Parameters.CustomParams<Workshop_Settings>().setPrintKAC)
                    updateKACAlarm();

                if (lastUpdateTime == 0)
                {
                    lastUpdateTime = Planetarium.GetUniversalTime();
                    ProcessItem(TimeWarp.deltaTime);
                    return;
                }

                //Get elapsed time
                double elapsedTime = Planetarium.GetUniversalTime() - lastUpdateTime;

                //Update last update time
                lastUpdateTime = Planetarium.GetUniversalTime();


                //If our elapsed time is > the background process interval, then we'll need to do some multiple processings.
                double timeRemaining = 0;
                while (elapsedTime > 0.1f) //kBackgroundProcessInterval)
                {
                    timeRemaining = ProcessItem(elapsedTime);
                    
                    if (_processedItem == null)
                        return;
                    if (elapsedTime == timeRemaining)
                        break;
                    elapsedTime = timeRemaining;
                    //elapsedTime -= kBackgroundProcessInterval;                    
                }

                //Process the remaining delta time
                if (elapsedTime > 0f)
                    ProcessItem(elapsedTime);
            }
            catch (Exception ex)
            {
                WorkshopUtils.LogError("OseModuleWorkshop_OnUpdate", ex);
            }
        }

        private void UpdateProductivity()
        {
            if (_processedItem != null && UseSpecializationBonus)
            {
                adjustedProductivity = WorkshopUtils.GetProductivityBonus(this.part, ExperienceEffect, SpecialistEfficiencyFactor, ProductivityFactor, WorkshopUtils.ProductivityType.printer);
            }
            if (_processedItem != null && wdc != null && HighLogic.CurrentGame.Parameters.CustomParams<Workshop_Settings>().unpackedAccelCausesDamage)
                adjustedProductivity /= (float)wdc.CurDamageImpact;
        }

        private double ProcessItem(double deltaTime)
        {
            Log.Info("Workshop.ProcessItem, deltaTime: " + deltaTime);
            double timeRemaining = deltaTime;

            if (manufacturingPaused)
            {
                Status = "Paused";
                return 0;
            }

            if (progress >= 100)
            {
                FinishManufacturing();
                timeRemaining -= 0.01f;
            }
            if (_processedItem != null)
            {
                timeRemaining = ExecuteManufacturing(deltaTime);
            }
            else
            {
                StartManufacturing();
                timeRemaining -= 0.01f;
            }

            return timeRemaining;
        }

        private void ApplyFilter()
        {
            if (_activeFilterId != _selectedFilterId || _activePage != _selectedPage || _oldSsearchText != _searchFilter.FilterText)
            {
                foreach (var item in _filteredItems.Items)
                {
                    item.DisableIcon();
                }

                if (_activeFilterId != _selectedFilterId)
                {
                    _activePage = 0;
                    _selectedPage = 0;
                }

                var selectedFilter = _filters[_selectedFilterId];
                if (string.IsNullOrEmpty(_searchFilter.FilterText))
                    _filteredItems = selectedFilter.Filter(_availableItems, _selectedPage * 30);
                else
                    _filteredItems = _searchFilter.Filter(_availableItems, _selectedPage * 30);
                _activeFilterId = _selectedFilterId;
                _activePage = _selectedPage;
            }
        }

        private void StartManufacturing()
        {
            var nextQueuedPart = _queue.Pop();
            if (nextQueuedPart != null)
            {
                _processedItem = nextQueuedPart;
                _processedBlueprint = WorkshopRecipeDatabase.ProcessPart(nextQueuedPart.Part, workshopRecipe);

                if (Animate && _heatAnimation != null && _workAnimation != null)
                {
                    StartCoroutine(StartAnimations());
                }
            }
        }

        private IEnumerator StartAnimations()
        {
            _heatAnimation.enabled = true;
            _heatAnimation.normalizedSpeed = 0.5f;
            while (_heatAnimation.normalizedTime < 1)
            {
                yield return null;
            }
            _heatAnimation.enabled = false;

            _workAnimation.enabled = true;
            _workAnimation.wrapMode = WrapMode.Loop;
            _workAnimation.normalizedSpeed = 0.5f;
        }

        private IEnumerator StopAnimations()
        {
            _heatAnimation.enabled = true;
            _heatAnimation.normalizedTime = 1;
            _heatAnimation.normalizedSpeed = -0.5f;

            _workAnimation.enabled = true;
            _workAnimation.wrapMode = WrapMode.Loop;
            _workAnimation.normalizedSpeed = 0.5f;

            while (_workAnimation.normalizedTime < 1)
            {
                yield return null;
            }
            _workAnimation.enabled = false;


            while (_heatAnimation.normalizedTime > 0)
            {
                yield return null;
            }
            _heatAnimation.enabled = false;
        }

        private double ExecuteManufacturing(double deltaTime)
        {
            Log.Info("ExecuteManufacturing, deltaTime: " + deltaTime);
            //Find the first resource that still needs to be processed.
            var resourceToConsume = _processedBlueprint.First(r => r.Processed < r.Units);

            //Determine the number of units of the resource to consume.
            var unitsToConsume = Math.Min(resourceToConsume.Units - resourceToConsume.Processed, deltaTime * adjustedProductivity / _processedBlueprint.Complexity);
            Log.Info("resourceToConsume: " + resourceToConsume.Name + ", unitsToConsume: " + unitsToConsume);
            if (part.protoModuleCrew.Count < MinimumCrew)
            {
                Status = "Not enough Crew to operate";
            }

            else if (AmountAvailable(UpkeepResource) < TimeWarp.deltaTime * UpkeepAmount)
            {
                Status = "Not enough " + UpkeepResource;
            }

            else if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER && WorkshopOptions.PrintingCostsFunds && Funding.Instance.Funds < _processedBlueprint.Funds)
            {
                Status = "Not enough funds to process";
            }

            else if (AmountAvailable(resourceToConsume.Name) < unitsToConsume)
            {
                Status = "Not enough " + resourceToConsume.Name;
            }
            else
            {
                Status = "Printing " + _processedItem.Part.title;
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER && WorkshopOptions.PrintingCostsFunds && _processedBlueprint.Funds > 0)
                {
                    Funding.Instance.AddFunds(-_processedBlueprint.Funds, TransactionReasons.Vessels);
                    _processedBlueprint.Funds = 0;
                }
                else
                {
                    _processedBlueprint.Funds = 0;
                }
                RequestResource(UpkeepResource, TimeWarp.deltaTime * UpkeepAmount);
                resourceToConsume.Processed += RequestResource(resourceToConsume.Name, unitsToConsume);
                progress = (float)(_processedBlueprint.GetProgress() * 100);
                Log.Info("progress: " + progress);
            }

            //Return time remaining
            //return deltaTime - unitsToConsume;


            double d = deltaTime - deltaTime * (unitsToConsume / (deltaTime * adjustedProductivity / _processedBlueprint.Complexity));
            Log.Info("ExecuteManufacturing, returning : " + d);
            return d;
        }

        private double AmountAvailable(string resource)
        {
            var res = PartResourceLibrary.Instance.GetDefinition(resource);
            double amount, maxAmount;
            part.GetConnectedResourceTotals(res.id, out amount, out maxAmount);
            return amount;
        }

        private float RequestResource(string resource, double amount)
        {
            var res = PartResourceLibrary.Instance.GetDefinition(resource);
            return (float)this.part.RequestResource(res.id, amount);
        }

        private void FinishManufacturing()
        {
            var destinationInventory = AddToContainer(_processedItem);
            if (destinationInventory != null)
            {
                ScreenMessages.PostScreenMessage("3D Printing of " + _processedItem.Part.title + " finished.", 5, ScreenMessageStyle.UPPER_CENTER);
                _processedItem.DisableIcon();
                _processedItem = null;
                _processedBlueprint = null;
                progress = 0;
                Status = "Online";

                if (Animate && _heatAnimation != null && _workAnimation != null)
                {
                    StartCoroutine(StopAnimations());
                }
            }
            else
            {
                Status = "Not enough free space";
            }

            deleteKACAlarm();
        }

        public override void OnInactive()
        {
            if (_showGui)
            {
                ContextMenuOpenWorkbench();
            }
            base.OnInactive();
        }

        private void OnVesselChange(Vessel v)
        {
            if (_showGui)
            {
                ContextMenuOpenWorkbench();
            }
            LoadMaxVolume();
        }

        private ModuleKISInventory AddToContainer(WorkshopItem item)
        {
            var inventories = KISWrapper.GetInventories(vessel);

            if (inventories == null || inventories.Count == 0)
            {
                throw new Exception("No KIS Inventory found!");
            }

            var freeInventories = inventories
                .Where(i => WorkshopUtils.HasFreeSpace(i, item))
                .Where(WorkshopUtils.HasFreeSlot)
                .Where(WorkshopUtils.IsOccupied)
                .ToArray();

            if (freeInventories.Any())
            {
                // first pass with favored inventories
                var favoredInventories = freeInventories
                    .Where(i => i.part.GetComponent<OseModuleInventoryPreference>() != null)
                    .Where(i => i.part.GetComponent<OseModuleInventoryPreference>().IsFavored).ToArray();

                foreach (var inventory in favoredInventories)
                {
                    var kisItem = inventory.AddItem(item.Part.partPrefab);
                    if (kisItem == null)
                    {
                        throw new Exception("Error adding item " + item.Part.name + " to inventory");
                    }
                    foreach (var resourceInfo in kisItem.GetResources())
                    {
                        if (WorkshopRecipeDatabase.HasResourceRecipe(resourceInfo.resourceName))
                        {
                            kisItem.SetResource(resourceInfo.resourceName, (int)resourceInfo.maxAmount);
                        }
                        else
                        {
                            kisItem.SetResource(resourceInfo.resourceName, 0);
                        }
                    }
                    return inventory;
                }

                // second pass with the rest
                foreach (var inventory in freeInventories)
                {
                    var kisItem = inventory.AddItem(item.Part.partPrefab);
                    if (kisItem == null)
                    {
                        throw new Exception("Error adding item " + item.Part.name + " to inventory");
                    }
                    foreach (var resourceInfo in kisItem.GetResources())
                    {
                        if (WorkshopRecipeDatabase.HasResourceRecipe(resourceInfo.resourceName))
                        {
                            kisItem.SetResource(resourceInfo.resourceName, (int)resourceInfo.maxAmount);
                        }
                        else
                        {
                            kisItem.SetResource(resourceInfo.resourceName, 0);
                        }
                    }
                    return inventory;
                }
            }
            return null;
        }

        // ReSharper disable once UnusedMember.Local => Unity3D
        // ReSharper disable once InconsistentNaming => Unity3D
        void OnGUI()
        {
            if (_showGui)
            {
                DrawWindow();
            }
        }

        GUIStyle window;
        bool windowInitted = false;

        private void DrawWindow()
        {
            if (!HighLogic.CurrentGame.Parameters.CustomParams<Workshop_MiscSettings>().useAlternateSkin)
                GUI.skin = HighLogic.Skin;
            else
                GUI.color = Color.grey;

            if (!windowInitted && HighLogic.CurrentGame.Parameters.CustomParams<Workshop_MiscSettings>().useAlternateSkin)
            {
                windowInitted = true;
                window = new GUIStyle(HighLogic.Skin.window);

                window.active.background = window.normal.background;

                Texture2D tex = window.normal.background; //.CreateReadable();

                var pixels = tex.GetPixels32();

                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i].a = 255;

                tex.SetPixels32(pixels); tex.Apply();
                //#if DEBUG
                //                tex.SaveToDisk("usermodified_window_bkg.png");
                //#endif
                // one of these apparently fixes the right thing
                // window.onActive.background =
                // window.onFocused.background =
                // window.onNormal.background =
                //window.onHover.background =
                window.active.background =
                window.focused.background =
                //window.hover.background =
                window.normal.background = tex;
            }

            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.skin.button.alignment = TextAnchor.MiddleCenter;

            if (!HighLogic.CurrentGame.Parameters.CustomParams<Workshop_MiscSettings>().useAlternateSkin)
                _windowPos = ClickThruBlocker.GUIWindow(GetInstanceID(), _windowPos, DrawWindowContents, "Workbench (Max Volume: " + _maxVolume + " litres - " + _filters[_activeFilterId] + ")");
            else
                _windowPos = ClickThruBlocker.GUIWindow(GetInstanceID(), _windowPos, DrawWindowContents, "Workbench (Max Volume:" + _maxVolume + " litres - " + _filters[_activeFilterId] + ")", window);

        }

        private void DrawWindowContents(int windowId)
        {
            _selectedFilterId = GUI.Toolbar(new Rect(15, 35, 615, 30), _selectedFilterId, _filterTextures);

            WorkshopItem mouseOverItem = null;

            mouseOverItem = DrawAvailableItems(mouseOverItem);
            mouseOverItem = DrawQueue(mouseOverItem);
            DrawMouseOverItem(mouseOverItem);

            DrawPrintProgress();

            if (GUI.Button(new Rect(_windowPos.width - 25, 5, 20, 20), "X"))
            {
                ContextMenuOpenWorkbench();
            }
            GUI.DragWindow();
        }

        private void DrawPrintProgress()
        {
            // Currently build item
            if (_processedItem != null)
            {
                if (_processedItem.Icon == null)
                {
                    _processedItem.EnableIcon(64);
                }
                GUI.Box(new Rect(190, 620, 50, 50), _processedItem.Icon.texture);
            }
            else
            {
                GUI.Box(new Rect(190, 620, 50, 50), "");
            }

            // Progressbar
            GUI.Box(new Rect(250, 620, 280, 50), "");
            if (progress >= 1)
            {
                var color = GUI.color;
                GUI.color = new Color(0, 1, 0, 1);
                GUI.Box(new Rect(250, 620, 280 * progress / 100, 50), "");
                GUI.color = color;
            }

            string progressText = "";
            if (_processedBlueprint != null)
                progressText = string.Format("Progress: {0:n1}%, T- ", progress) + KSPUtil.PrintTime(_processedBlueprint.GetBuildTime(WorkshopUtils.ProductivityType.printer, adjustedProductivity), 5, false);
            GUI.Label(new Rect(250, 620, 280, 50), " " + progressText);

            //Pause/resume production
            Texture2D buttonTexture = _pauseTexture;
            if (manufacturingPaused || _processedItem == null)
                buttonTexture = _playTexture;
            if (GUI.Button(new Rect(530, 620, 50, 50), buttonTexture) && _processedItem != null)
            {
                manufacturingPaused = !manufacturingPaused;
            }

            //Cancel production
            if (_processedItem == null)
                GUI.enabled = false;
            if (GUI.Button(new Rect(580, 620, 50, 50), _binTexture))
            {
                if (_confirmDelete)
                {
                    
                    _processedItem.DisableIcon();
                    _processedItem = null;
                    _processedBlueprint = null;

                    progress = 0;
                    manufacturingPaused = false;
                    Status = "Online";

                    if (Animate && _heatAnimation != null && _workAnimation != null)
                    {
                        StartCoroutine(StopAnimations());
                    }
                    _confirmDelete = false;
                }

                else
                {
                    _confirmDelete = true;
                    ScreenMessages.PostScreenMessage("Click the cancel button again to confirm cancelling current production", 5.0f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            GUI.enabled = true;
        }

        private WorkshopItem DrawAvailableItems(WorkshopItem mouseOverItem)
        {


            // Available Items
            const int itemRows = 10;
            const int itemColumns = 3;
            for (var y = 0; y < itemRows; y++)
            {
                for (var x = 0; x < itemColumns; x++)
                {
                    var left = 15 + x * 55;
                    var top = 70 + y * 55;
                    var itemIndex = y * itemColumns + x;
                    if (_filteredItems.Items.Length > itemIndex)
                    {
                        var item = _filteredItems.Items[itemIndex];
                        if (item.Icon == null)
                        {
                            item.EnableIcon(64);
                        }
                        if (GUI.Button(new Rect(left, top, 50, 50), item.Icon.texture))
                        {
                            _queue.Add(new WorkshopItem(item.Part));
                        }
                        if (Event.current.type == EventType.Repaint && new Rect(left, top, 50, 50).Contains(Event.current.mousePosition))
                        {
                            mouseOverItem = item;
                        }
                    }
                }
            }

            if (_activePage > 0)
            {
                if (GUI.Button(new Rect(15, 615, 75, 25), "Prev"))
                {
                    _selectedPage = _activePage - 1;
                }
            }

            if (_activePage < _filteredItems.MaxPages)
            {
                if (GUI.Button(new Rect(100, 615, 75, 25), "Next"))
                {
                    _selectedPage = _activePage + 1;
                }
            }

            // search box
            _oldSsearchText = _searchFilter.FilterText;
            GUI.Label(new Rect(15, 645, 65, 25), "Find: ", UI.UIStyles.StatsStyle);
            _searchFilter.FilterText = GUI.TextField(new Rect(75, 645, 100, 25), _searchFilter.FilterText);

            return mouseOverItem;
        }

        private WorkshopItem DrawQueue(WorkshopItem mouseOverItem)
        {
            const int queueRows = 4;
            const int queueColumns = 7;

            GUI.Box(new Rect(190, 345, 440, 270), "Queue", UI.UIStyles.QueueSkin);
            for (var y = 0; y < queueRows; y++)
            {
                for (var x = 0; x < queueColumns; x++)
                {
                    var left = 205 + x * 60;
                    var top = 370 + y * 60;
                    var itemIndex = y * queueColumns + x;
                    if (_queue.Count > itemIndex)
                    {
                        var item = _queue[itemIndex];
                        if (item.Icon == null)
                        {
                            item.EnableIcon(64);
                        }
                        if (GUI.Button(new Rect(left, top, 50, 50), item.Icon.texture))
                        {
                            _queue.Remove(item);
                        }
                        if (Event.current.type == EventType.Repaint && new Rect(left, top, 50, 50).Contains(Event.current.mousePosition))
                        {
                            mouseOverItem = item;
                        }
                    }
                }
            }

            return mouseOverItem;
            // Tooltip
        }

        private void DrawMouseOverItem(WorkshopItem mouseOverItem)
        {
            GUI.Box(new Rect(190, 70, 440, 270), "");
            if (mouseOverItem != null)
            {
                adjustedProductivity = WorkshopUtils.GetProductivityBonus(this.part, ExperienceEffect, SpecialistEfficiencyFactor, ProductivityFactor, WorkshopUtils.ProductivityType.printer);
                var blueprint = WorkshopRecipeDatabase.ProcessPart(mouseOverItem.Part);
                GUI.Box(new Rect(200, 80, 100, 100), mouseOverItem.Icon.texture);
                GUI.Box(new Rect(310, 80, 150, 100), WorkshopUtils.GetKisStats(mouseOverItem.Part), UI.UIStyles.StatsStyle);
                GUI.Box(new Rect(470, 80, 150, 100), blueprint.Print(WorkshopUtils.ProductivityType.printer, adjustedProductivity), UI.UIStyles.StatsStyle);
                GUI.Box(new Rect(200, 190, 420, 25), mouseOverItem.Part.title, UI.UIStyles.TitleDescriptionStyle);
                GUI.Box(new Rect(200, 220, 420, 110), mouseOverItem.Part.description, UI.UIStyles.TooltipDescriptionStyle);
             }
        }
    }
}
