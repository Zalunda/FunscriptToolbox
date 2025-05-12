-- FunscriptToolbox.MotionVectors LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"
virtual_actions = require "virtual_actions"
fullstroke = require "fullstroke"
configManager = require "gui_config_manager"
require "static_config" -- Defines PluginVersion, FTMVSFullPath etc.
require "gui_config_def" -- Defines guiConfigDefinition

configFullPath = ofs.ExtensionDir() .. "\\config.json"
mvsExtension = ".mvs32"

-- Global config variable that will hold the configuration table from configManager
-- These are effectively global because they are top-level in this main script.
config_manager = nil

connection = nil
virtualActions = {} -- scriptIdx -> virtual_actions instance
lastVideoFullPath = nil
lastMvsFullPath = nil
lastRequestError = nil
lastRequestErrorTooltip = nil
updateVersionMessage = nil


function init()
    printWithTime("Plugin Version:", PluginVersion)
    config_manager = configManager:new(guiConfigDefinition, configFullPath)
    connection = server_connection:new(FTMVSFullPath)
    sendCheckVersionRequest()
end

function update(delta)
    if connection then connection:processResponses() end
end

function scriptChange(scriptIdx)
    getVirtualActions(scriptIdx):unvirtualizeActionsBefore('scriptChange', player.CurrentTime())
end

function getVirtualActions(scriptIdx)
    if not scriptIdx then
        scriptIdx = ofs.ActiveIdx()
    end

    local va = virtualActions[scriptIdx]
    if not va then
        va = virtual_actions:new(scriptIdx, config)
        virtualActions[scriptIdx] = va
    end
    return va
end

function createRequest(service)
    return {
        ["$type"] = service
    }
end

function sendCreateRulesRequest(showUI)
    getVirtualActions():unvirtualizeActionsBefore('sendCreateRulesRequest', player.CurrentTime())
    getVirtualActions():removeAllVirtualActionsInTimelime('sendCreateRulesRequest')

    local scriptIndex = ofs.ActiveIdx()
    local script = ofs.Script(scriptIndex)
    local firstAction, indexBefore = script:closestActionAfter(player.CurrentTime() - 15)
    local lastAction, indexAfter = script:closestActionBefore(player.CurrentTime() + 0.001)
    local nextAction = script:closestActionAfter(player.CurrentTime() + 0.001);

    local candidates = {}
    for i = indexBefore, indexAfter do
        table.insert(candidates, script.actions[i])
    end

    local actionsToSend = fullstroke.getActionsForLastNHalfStrokes(
        candidates, 
        config_manager:getConfigValue("learn.MaxLearningStrokes") * 2)

    local request = createRequest("CreateRulesPluginRequest")

    local videoFullPath = player.CurrentVideo()
    local mvsFullPath = nil

    if lastVideoFullPath == videoFullPath then
        mvsFullPath = lastMvsFullPath
    else
        printWithTime('Trying to find ' .. mvsExtension .. ' file from videoFullPath...')
        local combinedPart = nil
        for part in string.gmatch(videoFullPath, "[^.]+") do
            combinedPart = combinedPart and combinedPart .. "." .. part or part
            local potentialMvsFullPath = combinedPart .. mvsExtension
            local file = io.open(potentialMvsFullPath, "r")
            if file then
                printWithTime('   FOUND ' .. potentialMvsFullPath)
                io.close(file)
                mvsFullPath = potentialMvsFullPath
            else
                printWithTime('   NOT FOUND ' .. potentialMvsFullPath)
            end
        end
    end

    if mvsFullPath then
        lastRequestError = nil
        lastVideoFullPath = videoFullPath
        lastMvsFullPath = mvsFullPath

        request.VideoFullPath = videoFullPath
        request.CurrentVideoTime = math.floor(player.CurrentTime() * 1000)
        request.MvsFullPath = mvsFullPath
        request.ScriptIndex = scriptIndex
        request.Actions = {}
        request.SelectedActions = {}
        request.ShowUI = showUI;
        request.TopMostUI = config_manager:getConfigValue("learn.TopMostUI");
        request.DefaultActivityFilter = config_manager:getConfigValue("learn.DefaultActivityFilter");
        request.DefaultQualityFilter = config_manager:getConfigValue("learn.DefaultQualityFilter");
        request.DefaultMinimumPercentageFilter = config_manager:getConfigValue("learn.DefaultMinimumPercentageFilter");
        request.MaximumMemoryUsageInMB = config_manager:getConfigValue("learn.MaximumMemoryUsageInMB");

        if nextAction then
            request.DurationToGenerateInSeconds = math.min(config_manager:getConfigValue("generate.MaximumDurationToGenerateInSeconds"), nextAction.at - player.CurrentTime())
        else
            request.DurationToGenerateInSeconds = config_manager:getConfigValue("generate.MaximumDurationToGenerateInSeconds")
        end
        request.MaximumStrokesDetectedPerSecond = config_manager:getConfigValue("generate.MaximumStrokesDetectedPerSecond");

        for _, action in ipairs(actionsToSend) do
            table.insert(request.Actions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
        end

        if script:hasSelection() then
            for _, indice in ipairs(script:selectedIndices()) do
                local action = script.actions[indice]
                table.insert(request.SelectedActions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
            end
        end

        connection:sendRequest(request, handleCreateRulesResponse)
    else
        lastRequestError = 'ERROR: Unable to find .mvs file for this video'
        lastRequestErrorTooltip = 'You need to create a .mvs file using FunscriptToolbox.exe for this video.\nSearched relative to: ' .. videoFullPath
    end
end

function handleCreateRulesResponse(response)
    if response.Actions then
        config_manager:resetPartialConfigToDefaultValues()

        getVirtualActions(response.ScriptIndex):init('handleCreateRulesResponse', response.Actions, response.FrameDurationInMs / 1000)
    end
end

function sendCheckVersionRequest()
    local request = createRequest("CheckVersionPluginRequest");
    request.PluginVersion = PluginVersion
    connection:sendRequest(request, handleCheckVersionResponse)
end

function handleCheckVersionResponse(response)
    if response.LastestVersion and PluginVersion ~= response.LastestVersion then
        updateVersionMessage = 'NEW PLUGIN VERSION AVAILABLE! ' .. PluginVersion .. ' => ' .. response.LastestVersion
        printWithTime(updateVersionMessage)
    end
end

function printWithTime(...)
    local localtime = os.date("*t", os.time())
    local formattedTime = string.format("%02d:%02d:%02d", localtime.hour, localtime.min, localtime.sec)

    local args = {...}
    table.insert(args, 1, formattedTime)
    print(table.unpack(args))
end

function getFormattedTime(timestamp)
    local secondsWithMilliseconds = timestamp % 60
    local seconds = math.floor(secondsWithMilliseconds)
    local minutes = math.floor(timestamp / 60) % 60
    local milliseconds = math.floor((secondsWithMilliseconds - seconds) * 1000)
    return string.format("%d:%02d.%03d", minutes, seconds, milliseconds)
end

--------------------------------------------------------------------
-- BINDINGS
--------------------------------------------------------------------
function binding.start_funscripttoolbox_motionvectors_without_ui()
    sendCreateRulesRequest(false)
end
function binding.start_funscripttoolbox_motionvectors_with_ui()
    sendCreateRulesRequest(true)
end
function binding.go_back_to_start()
    local start = getVirtualActions():getStartTime()
    if start then
        player.Seek(start)
    end
end
function isClosestActionTop()
	local script = ofs.Script(ofs.ActiveIdx())
	local firstAction, indexBefore = script:closestAction(player.CurrentTime())
	local nextAction = firstAction and script:closestActionAfter(firstAction.at)
	if firstAction and nextAction then
		return firstAction.pos > nextAction.pos
	else		
		return false
	end
end
function binding.smart_points_move_to_left()
    local targetPath = isClosestActionTop() and "TopPointsOffset" or "BottomPointsOffset"
    if config_manager:IncrementValueByStep(targetPath, -1) then
        updateVirtualPoints()
    end
end
function binding.smart_points_move_to_right()
    local targetPath = isClosestActionTop() and "TopPointsOffset" or "BottomPointsOffset"
    if config_manager:IncrementValueByStep(targetPath, 1) then
        updateVirtualPoints()
    end
end
function binding.all_points_move_to_left()
    local changed1 = config_manager:IncrementValueByStep("TopPointsOffset", -1)
    local changed2 = config_manager:IncrementValueByStep("BottomPointsOffset", -1)
    if changed1 or changed2 then updateVirtualPoints() end
end
function binding.all_points_move_to_right()
    local changed1 = config_manager:IncrementValueByStep("TopPointsOffset", 1)
    local changed2 = config_manager:IncrementValueByStep("BottomPointsOffset", 1)
    if changed1 or changed2 then updateVirtualPoints() end
end
function binding.top_points_move_to_left()
    if config_manager:IncrementValueByStep("TopPointsOffset", -1) then updateVirtualPoints() end
end
function binding.top_points_move_to_right()
    if config_manager:IncrementValueByStep("TopPointsOffset", 1) then updateVirtualPoints() end
end
function binding.bottom_points_move_to_left()
    if config_manager:IncrementValueByStep("BottomPointsOffset", -1) then updateVirtualPoints() end
end
function binding.bottom_points_move_to_right()
    if config_manager:IncrementValueByStep("BottomPointsOffset", 1) then updateVirtualPoints() end
end
function binding.min_position_move_up()
    if config_manager:IncrementValueByStep("MinimumPosition", 1) then updateVirtualPoints() end
end
function binding.min_position_move_down()
    if config_manager:IncrementValueByStep("MinimumPosition", -1) then updateVirtualPoints() end
end
function binding.max_position_move_up()
    if config_manager:IncrementValueByStep("MaximumPosition", 1) then updateVirtualPoints() end
end
function binding.max_position_move_down()
    if config_manager:IncrementValueByStep("MaximumPosition", -1) then updateVirtualPoints() end
end
function binding.min_percentage_filled_move_up()
    if config_manager:IncrementValueByStep("MinimumPercentageFilled", 1) then updateVirtualPoints() end
end
function binding.min_percentage_filled_move_down()
    if config_manager:IncrementValueByStep("MinimumPercentageFilled", -1) then updateVirtualPoints() end
end
function binding.center_move_up()
    if config_manager:IncrementValueByStep("AmplitudeCenter", 1) then updateVirtualPoints() end
end
function binding.center_move_down()
    if config_manager:IncrementValueByStep("AmplitudeCenter", -1) then updateVirtualPoints() end
end
function binding.center_top()
    config_manager:setConfigValue("AmplitudeCenter", 100)
    updateVirtualPoints()
end
function binding.center_bottom()
    config_manager:setConfigValue("AmplitudeCenter", 0)
    updateVirtualPoints()
end
function binding.extraamplitude_move_up()
    if config_manager:IncrementValueByStep("ExtraPercentage", 1) then updateVirtualPoints() end
end
function binding.extraamplitude_move_down()
    if config_manager:IncrementValueByStep("ExtraPercentage", -1) then updateVirtualPoints() end
end

function updateVirtualPoints()
    getVirtualActions():unvirtualizeActionsBefore('updateVirtualPoints', player.CurrentTime())
    getVirtualActions():update('updateVirtualPoints')
end
--------------------------------------------------------------------
-- GUI
--------------------------------------------------------------------
function gui()
    if updateVersionMessage then
        ofs.Separator()
        ofs.Text(updateVersionMessage)
        ofs.Tooltip('To update the plugin:\n  Start "--FSTB-Installation.bat" in FunscriptToolbox folder\n  Then reload the plugin, or restart OpenFunScripter')
        ofs.Separator()
        ofs.Separator()
    end
    if lastRequestError then
        ofs.Separator()
        ofs.Text(lastRequestError)
        ofs.Tooltip(lastRequestErrorTooltip)
        ofs.Separator()
        ofs.Separator()
    end
    local connectionStatus, connectionStatusTooltip = connection:getStatus(player.CurrentTime())
    ofs.Text("Connection: " .. connectionStatus)
    if connectionStatusTooltip then 
        ofs.Tooltip(connectionStatusTooltip) 
    end
    local virtualActionsStatus, virtualActionsStatusTooltip = getVirtualActions():getStatus(player.CurrentTime())
    ofs.Text("Virtual Actions: " .. virtualActionsStatus)
    if virtualActionsStatusTooltip then 
        ofs.Tooltip(virtualActionsStatusTooltip) 
    end
    ofs.Separator()

    -- renderConfigGui modifies config_manager.config directly.
    local anyConfigValueChanged, changedGroupTags = config_manager:renderConfigGui()

    if changedGroupTags["UpdateVirtualPoints"] then
        updateVirtualPoints()
    end
end