-- gui_config_manager.lua

-- Helper for string splitting by a single character delimiter
local function getPartsFromTargetPath(inputstr)
    if type(inputstr) ~= "string" or inputstr == "" then
        error("ConfigManager:getPartsFromTargetPath: pathString must be a non-empty string, got: " .. tostring(inputstr))
    end
    local parts = {}
    for part in string.gmatch(inputstr, "[^.]+") do
        table.insert(parts, part)
    end
    return parts
end

local ConfigManager = {}
ConfigManager.__index = ConfigManager

function ConfigManager:GetValueInTable(tbl, pathString, defaultValue)
    local pathParts = getPartsFromTargetPath(pathString)

    local currentParent = tbl
    for i = 1, #pathParts - 1 do -- Iterate to establish parent tables
        local part = pathParts[i]
        if not currentParent[part] or type(currentParent[part]) ~= "table" then
            currentParent[part] = {} -- Create missing intermediate table in 'tbl'
        end
        currentParent = currentParent[part]
    end
    if currentParent[pathParts[#pathParts]] ~= nil then
        return currentParent[pathParts[#pathParts]]
    else
        return defaultValue
    end
end

-- Resolves a pathString to its parent table and final key within self.config.
-- Creates intermediate tables in self.config if they don't exist. Populates cache.
-- This is a "private" helper.
function ConfigManager:resolveLeaf(pathString)
    if self.leafCache[pathString] then
        local cached = self.leafCache[pathString]
        return cached.parent, cached.key
    end

    local pathParts = getPartsFromTargetPath(pathString)
    local currentParent = self.config -- Always operates on self.config
    for i = 1, #pathParts - 1 do
        local part = pathParts[i]
        if not currentParent[part] or type(currentParent[part]) ~= "table" then
            currentParent[part] = {} -- Create missing intermediate table in self.config
        end
        currentParent = currentParent[part]
    end

    local finalKey = pathParts[#pathParts]
    self.leafCache[pathString] = { parent = currentParent, key = finalKey }
    return currentParent, finalKey
end

-- Constructor: Initializes and loads configuration.
function ConfigManager:new(definition, configFilePath)
    local o = setmetatable({}, ConfigManager)
    o.definition = definition
    o.configFilePath = configFilePath
    o.config = {}
    o.targetPathMap = {}
    o.defaultSetNames = {}
    o.leafCache = {}

    -- 1. Load raw data from file (if exists)
    local loadedConfigData = {}
    if o.configFilePath then
        local f = io.open(o.configFilePath, "r")
        if f then
            local content = f:read("*a")
            f:close()
            local success, decoded = pcall(json.decode, content) -- Use pcall for robust JSON parsing
            if success and type(decoded) == "table" then
                loadedConfigData = decoded
            else
                printWithTime("Warning (ConfigManager:new): Could not decode config file or not a table: " .. o.configFilePath)
            end
        end
    end

    -- 2. Process definition, merging with loadedConfigData to populate self.config
    local function processItemsAndBuild(itemsList)
        for _, itemDef in ipairs(itemsList) do
            if itemDef.type == "Header" then
                if itemDef.items then
                    processItemsAndBuild(itemDef.items)
                end
            else -- Process actual config items
                if itemDef.targetPath then
                    o.targetPathMap[itemDef.targetPath] = itemDef

                    local loadedValue = o:GetValueInTable(loadedConfigData, itemDef.targetPath, itemDef.defaultValue)
                    o:setConfigValue(itemDef.targetPath, loadedValue, true)

                    if itemDef.defaultValueSet then
                        o.defaultSetNames[itemDef.defaultValueSet] = true
                        itemDef.defaultStoragePath = "DEFAULT-" .. itemDef.defaultValueSet .. "." .. itemDef.targetPath
                        local loadedSessionDefault = o:GetValueInTable(loadedConfigData, itemDef.defaultStoragePath, itemDef.defaultValue)
                        o:setConfigValue(itemDef.defaultStoragePath, loadedSessionDefault, true)

                        itemDef.resetTargetPath = itemDef.targetPath .. "-Reset"
                        local loadedResetFlag = o:GetValueInTable(loadedConfigData, itemDef.resetTargetPath, true)
                        o:setConfigValue(itemDef.resetTargetPath, loadedResetFlag, true)

                        itemDef.resetDefaultStoragePath = "DEFAULT-" .. itemDef.defaultValueSet .. "." .. itemDef.resetTargetPath
                        local loadedSessionDefaultResetFlag = o:GetValueInTable(loadedConfigData, itemDef.resetDefaultStoragePath, true)
                        o:setConfigValue(itemDef.resetDefaultStoragePath, loadedSessionDefaultResetFlag, true)

                        -- implicit to resetFullConfigToDefaultValues
                        o:setConfigValue(itemDef.targetPath, loadedSessionDefault, true)
                        o:setConfigValue(itemDef.resetTargetPath, loadedSessionDefaultResetFlag, true)
                    end
                end
            end
        end
    end

    processItemsAndBuild(o.definition)

    return o
end

-- "Private" save method
function ConfigManager:_saveConfig()
    if not self.configFilePath then return end

    local encoded_data = json.encode(self.config)
    local requestFile = io.open(self.configFilePath , "w")
    if requestFile then
        requestFile:write(encoded_data)
        requestFile:close()
    else
        print("Error (ConfigManager): Could not open config file for writing: " .. self.configFilePath)
    end
end

-- Public getter for the managed config table.
function ConfigManager:getConfigValue(pathString)
    local parent, key = self:resolveLeaf(pathString)
    if parent[key] == nil then error("Cannot find value for " .. pathString) end
    return parent[key]
end

function ConfigManager:IsLogEnabled()
    return self:getConfigValue("EnableLogs")
end

-- Public setter for the managed config table.
-- Saves config automatically if a value changes, unless skipSave is true.
function ConfigManager:setConfigValue(pathString, value, skipSave)
    local parent, key = self:resolveLeaf(pathString)
    if parent[key] ~= value then
        parent[key] = value
        if not skipSave then
            self:_saveConfig()
        end
        return true
    else 
        return false
    end
end

-- Internal helper for resetting config values to defaults
-- Don't Auto-saves.
function ConfigManager:_internalResetConfigToDefaultValues(defaultSetName, resetType)
    local changed = false
    for targetPath, itemDef in pairs(self.targetPathMap) do
        if itemDef.defaultValueSet and (defaultSetName == nil or itemDef.defaultValueSet == defaultSetName) then
            local needsReset = false

            if resetType == "full" then
                needsReset = true
            elseif resetType == "partial" then
                local resetFlag = self:getConfigValue(itemDef.resetTargetPath)
                if resetFlag then
                    needsReset = true
                end
            end

            if needsReset then
                changed = changed or self:setConfigValue(targetPath, self:getConfigValue(itemDef.defaultStoragePath), true)
                changed = changed or self:setConfigValue(itemDef.resetTargetPath, self:getConfigValue(itemDef.resetDefaultStoragePath), true)
            end
        end
    end
    return changed
end

-- Resets specified (or all) default sets to their stored default values completely. Auto-saves.
function ConfigManager:resetFullConfigToDefaultValues(defaultSetName)
    if self:_internalResetConfigToDefaultValues(defaultSetName, "full") then
        self:_saveConfig()
    end
end

-- Resets specified (or all) default sets to their stored default values based on reset flags. Auto-saves.
function ConfigManager:resetPartialConfigToDefaultValues(defaultSetName)
    if self:_internalResetConfigToDefaultValues(defaultSetName, "partial") then
        self:_saveConfig()
    end
end

function ConfigManager:applyClampingToConfigValues()
    local changedByClamping = false
    for path, itemDef in pairs(self.targetPathMap) do
        if itemDef.targetPath and (itemDef.min ~= nil or itemDef.customClamp) then
            local currentValue = self:getConfigValue(itemDef.targetPath)
            if currentValue == nil then goto continue end

            local clampedValue = currentValue
            if itemDef.customClamp then
                clampedValue = itemDef.customClamp(currentValue)
            elseif itemDef.min ~= nil and itemDef.max ~= nil then
                clampedValue = clamp(currentValue, itemDef.min, itemDef.max)
            end

            if clampedValue ~= currentValue then
                self:setConfigValue(itemDef.targetPath, clampedValue)
                changedByClamping = true
            end
        end
        ::continue::
    end
    return changedByClamping
end

function ConfigManager:IncrementValueByStep(targetPathString, numSteps)
    local itemDef = self.targetPathMap[targetPathString]
    if not itemDef or not itemDef.targetPath then error() end

    local currentValue = self:getConfigValue(itemDef.targetPath)
    if currentValue == nil then error() end

    local stepValue = itemDef.step or 1
    if itemDef.stepFunction then
        stepValue = itemDef.stepFunction(currentValue)
    end

    local newValue = currentValue + (stepValue * numSteps)

    if itemDef.customClamp then
        newValue = itemDef.customClamp(newValue)
    elseif itemDef.min ~= nil and itemDef.max ~= nil then
        newValue = clamp(newValue, itemDef.min, itemDef.max)
    end

    if newValue ~= currentValue then
        self:setConfigValue(itemDef.targetPath, newValue)
        return true
    end
    return false
end

function ConfigManager:renderGuiItem(itemDef)
    local valueChanged = false
    local otherControlChanged = false
    local itemGroupTags = {}

    if itemDef.hidden then return false, false, itemGroupTags end

    if itemDef.type == "CustomControlsSet" then
        if itemDef.createControlsFunction then
            local customChanged = itemDef.createControlsFunction(self) -- Pass self if custom controls need to call setConfigValue
            if customChanged and itemDef.groupTags then
                for _, tag in ipairs(itemDef.groupTags) do itemGroupTags[tag] = true end
            end
            return customChanged, false, itemGroupTags
        end
    elseif itemDef.type == "ManageDefaultValueSet" then
        local defaultSetName = itemDef.defaultSetName
        ofs.Text('Default:')
        local isDiffFromDefault = false

        for path, subItemDef in pairs(self.targetPathMap) do
            if subItemDef.defaultValueSet == defaultSetName then
                if self:getConfigValue(subItemDef.targetPath) ~= self:getConfigValue(subItemDef.defaultStoragePath) then
                    isDiffFromDefault = true
                    break
                end
                if self:getConfigValue(subItemDef.resetTargetPath) ~= self:getConfigValue(subItemDef.resetDefaultStoragePath) then
                    isDiffFromDefault = true
                    break
                end
            end
        end

        ofs.BeginDisabled(not isDiffFromDefault)
        ofs.SameLine()
        if ofs.Button("Set##" .. itemDef.defaultSetName) then
            for path, subItemDef in pairs(self.targetPathMap) do
                if subItemDef.defaultValueSet == defaultSetName then
                    self:setConfigValue(subItemDef.defaultStoragePath, self:getConfigValue(subItemDef.targetPath))
                    self:setConfigValue(subItemDef.resetDefaultStoragePath, self:getConfigValue(subItemDef.resetTargetPath))
                    otherControlChanged = true
                    if subItemDef.groupTags then
                        for _, tag in ipairs(subItemDef.groupTags) do itemGroupTags[tag] = true end
                    end
                end
            end
        end
        ofs.SameLine()
        if ofs.Button("Reset##" .. itemDef.defaultSetName) then
            self:resetFullConfigToDefaultValues(defaultSetName)
            otherControlChanged = true
            for path, subItemDef in pairs(self.targetPathMap) do
                if subItemDef.defaultValueSet == defaultSetName and subItemDef.groupTags then
                    for _, tag in ipairs(subItemDef.groupTags) do itemGroupTags[tag] = true end
                end
            end
        end
        ofs.EndDisabled()
        return valueChanged, otherControlChanged, itemGroupTags
    end

    local currentValue = self:getConfigValue(itemDef.targetPath)
    local newValueForInput = currentValue

    if itemDef.defaultValueSet then
        local resetFlagId = "R##" .. itemDef.targetPath
        local currentResetFlag = self:getConfigValue(itemDef.resetTargetPath)
        local newResetFlag, changedByResetCheckbox
        newResetFlag, changedByResetCheckbox = ofs.Checkbox(resetFlagId, currentResetFlag)
        if changedByResetCheckbox then
            self:setConfigValue(itemDef.resetTargetPath, newResetFlag)
            otherControlChanged = true
            if itemDef.groupTags then
                 for _, tag in ipairs(itemDef.groupTags) do itemGroupTags[tag] = true end
            end
        end
        ofs.SameLine()
    end

    local changedByMainInput = false
    local valueFromInput = newValueForInput

    if itemDef.type == "InputInt" then
        local step = itemDef.step or 1
        if itemDef.stepFunction then step = itemDef.stepFunction(currentValue) end
        valueFromInput, changedByMainInput = ofs.InputInt(itemDef.label, newValueForInput, step)
    elseif itemDef.type == "InputFloat" then
        local step = itemDef.step or 1.0
        if itemDef.stepFunction then step = itemDef.stepFunction(currentValue) end
        valueFromInput, changedByMainInput = ofs.Input(itemDef.label, newValueForInput, step)
    elseif itemDef.type == "Checkbox" then
        valueFromInput, changedByMainInput = ofs.Checkbox(itemDef.label, newValueForInput)
    else
        if itemDef.type ~= "Header" then
            ofs.Text("Unknown/unhandled control type: " .. itemDef.type)
        end
    end

    if changedByMainInput then
        local finalNewValue = valueFromInput
        if itemDef.customClamp then
            finalNewValue = itemDef.customClamp(valueFromInput)
        elseif itemDef.min ~= nil and itemDef.max ~= nil then
            finalNewValue = clamp(valueFromInput, itemDef.min, itemDef.max)
        end
        
        if finalNewValue ~= currentValue then
            self:setConfigValue(itemDef.targetPath, finalNewValue)
            valueChanged = true
            if itemDef.groupTags then
                 for _, tag in ipairs(itemDef.groupTags) do itemGroupTags[tag] = true end
            end
        end
    end

    if itemDef.shortcuts then
        for _, shortcut in ipairs(itemDef.shortcuts) do
            ofs.SameLine()
            if ofs.Button(shortcut.label .. "##" .. itemDef.targetPath .. shortcut.label) then
                local valueToSet = shortcut.value
                if itemDef.customClamp then valueToSet = itemDef.customClamp(valueToSet)
                elseif itemDef.min ~= nil and itemDef.max ~= nil then
                     valueToSet = clamp(valueToSet, itemDef.min, itemDef.max)
                end

                if self:getConfigValue(itemDef.targetPath) ~= valueToSet then
                    self:setConfigValue(itemDef.targetPath, valueToSet)
                    valueChanged = true
                    if itemDef.groupTags then
                         for _, tag in ipairs(itemDef.groupTags) do itemGroupTags[tag] = true end
                    end
                end
            end
        end
    end
    
    return valueChanged, otherControlChanged, itemGroupTags
end

function ConfigManager:renderRecursive(itemsList)
    local anyConfigValueChangedThisLevel = false
    local changedGroupTagsThisLevel = {}

    for _, itemOrGroupDef in ipairs(itemsList) do
        if itemOrGroupDef.type == "Header" then
            local headerId = "Header##" .. itemOrGroupDef.header
            if not ofs.CollapsingHeader or ofs.CollapsingHeader(itemOrGroupDef.header, headerId) then
                if itemOrGroupDef.items then
                    local groupChanged, groupTags = self:renderRecursive(itemOrGroupDef.items)
                    if groupChanged then anyConfigValueChangedThisLevel = true end
                    for tag, _ in pairs(groupTags) do changedGroupTagsThisLevel[tag] = true end
                end
                ofs.Separator()
            end
        else
            local mainValChanged, otherCtrlChanged, itemTags = self:renderGuiItem(itemOrGroupDef)
            if mainValChanged or otherCtrlChanged then
                anyConfigValueChangedThisLevel = true
            end
            for tag, _ in pairs(itemTags) do
                changedGroupTagsThisLevel[tag] = true
            end
        end
    end
    return anyConfigValueChangedThisLevel, changedGroupTagsThisLevel
end

function ConfigManager:renderConfigGui()
    return self:renderRecursive(self.definition)
end

return ConfigManager