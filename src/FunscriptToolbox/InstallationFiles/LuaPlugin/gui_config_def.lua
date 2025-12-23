guiConfigDefinition = {
    {
        type = "Header",
        header = "Virtual Actions Settings",
        items = {
            {
                type = "CustomControlsSet",
                createControlsFunction = function()
                    local configChangedByCustomControl = false
                    if ofs.Button("Create") then sendCreateRulesRequest(config_manager:getConfigValue("learn.ShowUIOnCreate")) end
                    ofs.SameLine()
                    if ofs.Button("Hide") then getVirtualActions():removeAllVirtualActionsInTimelime('gui_custom') end
                    ofs.SameLine()
                    if ofs.Button("Show") then updateVirtualPoints() end
                    ofs.SameLine()
                    if ofs.Button("Delete") then
                        getVirtualActions():unvirtualizeActionsBefore('gui_custom', player.CurrentTime())
                        getVirtualActions():deleteAllVirtualActions('gui_custom')
                    end
                    if ofs.Button("Go back to start") then binding.go_back_to_start() end
                    return configChangedByCustomControl
                end,
                groupTags = {"ConfigMisc"}
            },
            {
                type = "Header",
                header = "    1. Learn from script",
                items = {
                    { 
                        label = "Maximum learning strokes",
                        targetPath = "learn.MaxLearningStrokes",
                        type = "InputInt",
                        defaultValue = 3,
                        step = 1, min = 0, max = 30
                    },
                    { 
                        label = "Nb frames to ignore",
                        targetPath = "learn.NbFramesToIgnoreAroundAction",
                        type = "InputInt",
                        defaultValue = 3,
                        step = 1, min = 0, max = 10
                    },
                    { 
                        label = "Show UI",
                        targetPath = "learn.ShowUIOnCreate",
                        type = "Checkbox", 
                        defaultValue = true,
                    },
                    { 
                        label = "TopMost UI",
                        targetPath = "learn.TopMostUI",
                        type = "Checkbox", 
                        defaultValue = true
                    },
                    { 
                        label = "Default Activity Filter",
                        targetPath = "learn.DefaultActivityFilter",
                        type = "InputInt",
                        defaultValue = 60,
                        step = 5, min = 0, max = 100
                    },
                    { 
                        label = "Default Quality Filter",
                        targetPath = "learn.DefaultQualityFilter", 
                        type = "InputInt",
                        defaultValue = 90,
                        step = 5, min = 50, max = 100 
                    },
                    { 
                        label = "Default Min % Filter",
                        targetPath = "learn.DefaultMinimumPercentageFilter",
                        type = "InputFloat", 
                        defaultValue = 0.0, 
                        stepFunction = function(cv) 
                            return cv <= 2 and 0.1 or 1 
                        end, 
                        min = 0, max = 100 
                    },
                    { 
                        label = "Maximum Memory Usage for frame cache (MB)",
                        targetPath = "learn.MaximumMemoryUsageInMB", 
                        type = "InputInt", 
                        defaultValue = 1000, 
                        step = 50, min = 0, max = 100000 
                    }
                }
            },
            {
                type = "Header", 
                header = "    2. Generate Actions",
                items = {
                    { 
                        label = "Maximum Strokes per sec",
                        targetPath = "generate.MaximumStrokesDetectedPerSecond", 
                        type = "InputFloat", 
                        defaultValue = 3.0, 
                        step = 0.5, min = 1.0, max = 5.0 
                    },
                    { 
                        label = "% of high velocity frames to keep",
                        targetPath = "generate.PercentageOfFramesToKeep", 
                        type = "InputInt", 
                        defaultValue = 70, 
                        step = 10, min = 0, max = 100
                    },
                    { 
                        label = "Maximum Generation (sec)",
                        targetPath = "generate.MaximumDurationToGenerateInSeconds", 
                        type = "InputInt", 
                        defaultValue = 120, 
                        step = 10, min = 20, max = 100000 
                    }                 
                }
            },
            {
                type = "Header",
                header = "    3. Amplitude Adjustments",
                items = {
                    {
                        label = "Min Pos",
                        targetPath = "amplitude.MinimumPosition",
                        type = "InputInt",
                        defaultValue = 0,
                        defaultValueSet = "Amplitude",
                        groupTags = {"UpdateVirtualPoints"},
                        step = 5, min = 0, max = 99,
                        shortcuts = {{label = "0", value = 0}}
                    },
                    {
                        label = "Max Pos",
                        targetPath = "amplitude.MaximumPosition",
                        type = "InputInt",
                        defaultValue = 100,
                        defaultValueSet = "Amplitude",
                        groupTags = {"UpdateVirtualPoints"},
                        step = 5,
                        customClamp = function(val)
                            return clamp(val, config_manager:getConfigValue("amplitude.MinimumPosition") + 5, 100)
                        end,
                        shortcuts = {{label = "100", value = 100}}
                    },
                    {
                        label = "Center Pos %",
                        targetPath = "amplitude.Center",
                        type = "InputInt",
                        defaultValue = 50,
                        defaultValueSet = "Amplitude",
                        groupTags = {"UpdateVirtualPoints"},
                        step = 10, min = 0, max = 100,
                        shortcuts = {{label = "0", value = 0}, {label = "50", value = 50}, {label = "100", value = 100}}
                    },
                    {
                        label = "Min % filled",
                        targetPath = "amplitude.MinimumPercentageFilled",
                        type = "InputInt",
                        defaultValue = 0,
                        defaultValueSet = "Amplitude",
                        groupTags = {"UpdateVirtualPoints"},
                        step = 10, min = 0, max = 100,
                        shortcuts = {{label = "0", value = 0}, {label = "50", value = 50}}
                    },
                    {
                        label = "Extra %",
                        targetPath = "amplitude.ExtraPercentage",
                        type = "InputInt",
                        defaultValue = 0,
                        defaultValueSet = "Amplitude",
                        groupTags = {"UpdateVirtualPoints"},
                        step = 10, min = 0, max = 100,
                        shortcuts = {{label = "0", value = 0}, {label = "100", value = 100}}
                    },
                    {
                        type = "ManageDefaultValueSet",
                        defaultSetName = "Amplitude"
                    }
                }
            }
        }
    },
    {
        type = "Header", header = "Miscellaneous Config",
        items = {
            {
                label = "Enable Logs",
                targetPath = "EnableLogs", 
                type = "Checkbox", 
                defaultValue = true
            }
        }
    }
}