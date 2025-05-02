-- FunscriptToolbox.MotionVectors LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"
virtual_actions = require "virtual_actions"
require "static_config"

configFullPath = ofs.ExtensionDir() .. "\\config.json"
mvsExtension = ".mvs32"

config = {}
connection = nil
virtualActions = {}
lastVideoFullPath = nil
lastMvsFullPath = nil

function init()
    printWithTime("Plugin Version:", PluginVersion)

	loadOrCreateConfig()
	connection = server_connection:new(FTMVSFullPath, config.EnableLogs)
	
	sendCheckVersionRequest()
end

function loadOrCreateConfig()

	local f = io.open(configFullPath)
	local loaded
    if f then
		local content = f:read("*a")
		f:close()
		loaded = json.decode(content)
	else
		loaded = {}
    end

	if not config.adjustVps then config.adjustVps = {} end
	if not config.shared then config.shared = {} end
	
	if not loaded.config then loaded.config = {} end
	if not loaded.config.adjustVps then loaded.config.adjustVps = {} end
	if not loaded.config.shared then loaded.config.shared = {} end

	config.EnableLogs 								= loaded.EnableLogs == nil and true or loaded.EnableLogs
	config.ShowUIOnCreate							= loaded.ShowUIOnCreate == nil and true or loaded.ShowUIOnCreate
	
	config.adjustVps.TopPointsOffset 				= loaded.adjustVps.TopPointsOffset 				or 0
	config.adjustVps.TopPointsOffsetReset			= loaded.adjustVps.TopPointsOffsetReset == nil and true or loaded.adjustVps.TopPointsOffsetReset
	config.adjustVps.BottomPointsOffset 			= loaded.adjustVps.BottomPointsOffset 			or 0
	config.adjustVps.BottomPointsOffsetReset		= loaded.adjustVps.BottomPointsOffsetReset == nil and true or loaded.adjustVps.BottomPointsOffsetReset
	config.adjustVps.MinimumPosition 				= loaded.adjustVps.MinimumPosition 				or 0
	config.adjustVps.MinimumPositionReset			= loaded.adjustVps.MinimumPositionReset == nil and true or loaded.adjustVps.MinimumPositionReset
	config.adjustVps.MaximumPosition 				= loaded.adjustVps.MaximumPosition 				or 100
	config.adjustVps.MaximumPositionReset			= loaded.adjustVps.MaximumPositionReset == nil and true or loaded.adjustVps.MaximumPositionReset
	config.adjustVps.MinimumPercentageFilled 		= loaded.adjustVps.MinimumPercentageFilled 		or 0
	config.adjustVps.MinimumPercentageFilledReset	= loaded.adjustVps.MinimumPercentageFilledReset == nil and true or loaded.adjustVps.MinimumPercentageFilledReset
	config.adjustVps.AmplitudeCenter 				= loaded.adjustVps.AmplitudeCenter 				or 50
	config.adjustVps.AmplitudeCenterReset			= loaded.adjustVps.AmplitudeCenterReset == nil and true or loaded.adjustVps.AmplitudeCenterReset
	config.adjustVps.ExtraAmplitudePercentage 		= loaded.adjustVps.ExtraAmplitudePercentage 		or 0
	config.adjustVps.ExtraAmplitudePercentageReset	= loaded.adjustVps.ExtraAmplitudePercentageReset == nil and true or loaded.adjustVps.ExtraAmplitudePercentageReset

	config.shared.MaximumMemoryUsageInMB 			 = loaded.shared.MaximumMemoryUsageInMB 			    or 1000
	config.shared.LearningDurationInSeconds 		 = loaded.shared.LearningDurationInSeconds 		    or 10
	config.shared.DefaultActivityFilter 			 = loaded.shared.DefaultActivityFilter 			    or 60
	config.shared.DefaultQualityFilter 				 = loaded.shared.DefaultQualityFilter 			    or 90
	config.shared.DefaultMinimumPercentageFilter 	 = loaded.shared.DefaultMinimumPercentageFilter 		or 5
	config.shared.MaximumDurationToGenerateInSeconds = loaded.shared.MaximumDurationToGenerateInSeconds	or 120
	config.shared.MaximumNbStrokesDetectedPerSecond  = loaded.shared.MaximumNbStrokesDetectedPerSecond	or 3.0
	config.shared.TopMostUI							 = loaded.shared.TopMostUI == nil and true or loaded.shared.TopMostUI

	fullResetAdjustConfigToDefault()
end

function fullResetAdjustConfigToDefault()
	config.TopPointsOffset 							= config.adjustVps.TopPointsOffset
	config.BottomPointsOffset 						= config.adjustVps.BottomPointsOffset
	config.MinimumPosition 							= config.adjustVps.MinimumPosition
	config.MaximumPosition 							= config.adjustVps.MaximumPosition
	config.MinimumPercentageFilled 					= config.adjustVps.MinimumPercentageFilled
	config.AmplitudeCenter 							= config.adjustVps.AmplitudeCenter
	config.ExtraAmplitudePercentage 				= config.adjustVps.ExtraAmplitudePercentage
end

function partialResetAdjustConfigToDefault()
	if config.adjustVps.TopPointsOffsetReset then
		config.TopPointsOffset 						= config.adjustVps.TopPointsOffset
	end
	if config.adjustVps.BottomPointsOffsetReset then
		config.BottomPointsOffset 					= config.adjustVps.BottomPointsOffset
	end
	if config.adjustVps.MinimumPositionReset then
		config.MinimumPosition 						= config.adjustVps.MinimumPosition
	end
	if config.adjustVps.MaximumPositionReset then
		config.MaximumPosition 						= config.adjustVps.MaximumPosition
	end
	if config.adjustVps.MinimumPercentageFilledReset then
		config.MinimumPercentageFilled 				= config.adjustVps.MinimumPercentageFilled
	end
	if config.adjustVps.AmplitudeCenterReset then
		config.AmplitudeCenter 						= config.adjustVps.AmplitudeCenter
	end
	if config.adjustVps.ExtraAmplitudePercentageReset then
		config.ExtraAmplitudePercentage 			= config.adjustVps.ExtraAmplitudePercentage
	end
end

function saveConfig()
	local encoded_data = json.encode(config)
 	local requestFile = io.open(configFullPath , "w")
 	requestFile:write(encoded_data)
 	requestFile:close()
end


function update(delta)
	connection:processResponses()
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
		["$type"] = service,
		SharedConfig = config.shared
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
    
    local actionsToSend = getActionsForLastNHalfStrokes(candidates, config.shared.NumberHalfStrokesSample or 6)
    
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
            potentialMvsFullPath = combinedPart .. mvsExtension
            local file = io.open(potentialMvsFullPath, "r")
            if file then
                printWithTime('   FOUND ' .. potentialMvsFullPath)
                io.close(file)
                mvsFullPath = potentialMvsFullPath
            else
                printWithTime('   ' .. potentialMvsFullPath)
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

        if nextAction then
            request.DurationToGenerateInSeconds = math.min(config.shared.MaximumDurationToGenerateInSeconds, nextAction.at - player.CurrentTime())
        else
            request.DurationToGenerateInSeconds = config.shared.MaximumDurationToGenerateInSeconds
        end
        request.ShowUI = showUI
        
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
        lastRequestErrorTooltip = 'You need to create a .mvs file using FunscriptToolbox.exe for this video.'
    end
end

-- TODO MOVE and/or rename
function getActionsForLastNHalfStrokes(candidates, maxNumberHalfStrokes)
    -- Need at least 2 actions to detect direction
    if #candidates < 2 then
        return candidates
    end
    
    local actionsToInclude = {}
    local directionChanges = 0
    local previousDirection = nil
    
    -- Start from the most recent action (end of array) and work backwards
    -- Always include the newest action
    table.insert(actionsToInclude, candidates[#candidates])
    
    for i = #candidates - 1, 1, -1 do
        local current = candidates[i]
        local next = candidates[i+1]  -- The action after (more recent in time)
        
        -- Determine direction (still from older to newer perspective)
        local direction = nil
        if current.pos < next.pos then
            direction = "up"
        elseif current.pos > next.pos then
            direction = "down"
        else
            direction = "neutral"
        end
        
        -- Check for direction change (skip neutral)
        if direction ~= "neutral" then
            if previousDirection ~= nil and direction ~= previousDirection then
                directionChanges = directionChanges + 1
                
                -- If we found enough direction changes, we're done
                if directionChanges >= maxNumberHalfStrokes then
                    break
                end
            end
            
            -- Update previous direction
            previousDirection = direction
        end

        -- Include this action
        table.insert(actionsToInclude, 1, current)  -- Insert at beginning to maintain chronological order        
    end
    
    return actionsToInclude
end

function handleCreateRulesResponse(response)
	if response.Actions then
		partialResetAdjustConfigToDefault()
			
		getVirtualActions(response.ScriptIndex):init('handleCreateRulesResponse', response.Actions, response.FrameDurationInMs / 1000)
	end
end

function sendCheckVersionRequest()

 	local request = createRequest("CheckVersionPluginRequest");
	request.PluginVersion = PluginVersion
	connection:sendRequest(request, handleCheckVersionResponse)
end

function handleCheckVersionResponse(response)
	if PluginVersion ~= response.LastestVersion then
		updateVersionMessage = 'NEW PLUGIN VERSION AVAILABLE! ' .. PluginVersion .. ' => ' .. response.LastestVersion
		printWithTime(updateVersionMessage)
	end
end

function printWithTime(...)
	local localtime = os.date("*t", time) -- get local date and time table
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
	if isClosestActionTop() then
		config.TopPointsOffset = config.TopPointsOffset - 1
	else
		config.BottomPointsOffset = config.BottomPointsOffset - 1
	end	
	updateVirtualPoints()
end 
function binding.smart_points_move_to_right()
	if isClosestActionTop() then
		config.TopPointsOffset = config.TopPointsOffset + 1
	else
		config.BottomPointsOffset = config.BottomPointsOffset + 1
	end	
	updateVirtualPoints()
end 
function binding.all_points_move_to_left()
	config.TopPointsOffset = config.TopPointsOffset - 1
	config.BottomPointsOffset = config.TopPointsOffset - 1
	updateVirtualPoints()
end 
function binding.all_points_move_to_right()
	config.TopPointsOffset = config.TopPointsOffset + 1
	config.BottomPointsOffset = config.TopPointsOffset + 1
	updateVirtualPoints()
end 
function binding.top_points_move_to_left()
	config.TopPointsOffset = config.TopPointsOffset - 1
	updateVirtualPoints()
end 
function binding.top_points_move_to_right()
	config.TopPointsOffset = config.TopPointsOffset + 1
	updateVirtualPoints()
end 
function binding.bottom_points_move_to_left()
	config.BottomPointsOffset = config.BottomPointsOffset - 1
	updateVirtualPoints()
end 
function binding.min_position_move_up()
	config.MinimumPosition = config.MinimumPosition + 5
	updateVirtualPoints()
end 
function binding.min_position_move_down()
	config.MinimumPosition = config.MinimumPosition - 5
	updateVirtualPoints()
end 
function binding.max_position_move_up()
	config.MaximumPosition = config.MinimumPosition + 5
	updateVirtualPoints()
end 
function binding.max_position_move_down()
	config.MaximumPosition = config.MinimumPosition - 5
	updateVirtualPoints()
end 
function binding.min_percentage_filled_move_up()
	config.MinimumPercentageFilled = config.MinimumPercentageFilled + 10
	updateVirtualPoints()
end 
function binding.min_percentage_filled_move_down()
	config.MinimumPercentageFilled = config.MinimumPercentageFilled - 10
	updateVirtualPoints()
end 
function binding.center_move_up()
	config.AmplitudeCenter = config.AmplitudeCenter + 10
	updateVirtualPoints()
end 
function binding.center_move_down()
	config.AmplitudeCenter = config.AmplitudeCenter - 10
	updateVirtualPoints()
end 
function binding.center_top()
	config.AmplitudeCenter = 100
	updateVirtualPoints()
end 
function binding.center_bottom()
	config.AmplitudeCenter = 0
	updateVirtualPoints()
end 
function binding.extraamplitude_move_up()
	config.ExtraAmplitudePercentage = config.ExtraAmplitudePercentage + 10
	updateVirtualPoints()
end 
function binding.extraamplitude_move_down()
	config.ExtraAmplitudePercentage = config.ExtraAmplitudePercentage - 10
	updateVirtualPoints()
end 
function binding.reset_to_default()
	fullResetAdjustConfigToDefault()
	updateVirtualPoints()
end 
function updateVirtualPoints()
	config.TopPointsOffset = clamp(config.TopPointsOffset, -10, 10)
	config.BottomPointsOffset = clamp(config.BottomPointsOffset, -10, 10)
	config.MinimumPosition = clamp(config.MinimumPosition, 0, 95)
	config.MaximumPosition = clamp(config.MaximumPosition, config.MinimumPosition + 5, 100)
	config.MinimumPercentageFilled = clamp(config.MinimumPercentageFilled, 0, 100)
	config.AmplitudeCenter = clamp(config.AmplitudeCenter, 0, 100)
	config.ExtraAmplitudePercentage = clamp(config.ExtraAmplitudePercentage, 0, 1000)
	getVirtualActions():unvirtualizeActionsBefore('updateVirtualPoints', player.CurrentTime())
	getVirtualActions():update('updateVirtualPoints')
	saveConfig()
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
	
	local showUIOnCreateChanged = false
	if not ofs.CollapsingHeader or ofs.CollapsingHeader("Virtual Actions") then
		if ofs.Button("Create") then			
			sendCreateRulesRequest(config.ShowUIOnCreate)
		end
		ofs.SameLine()
		config.ShowUIOnCreate, showUIOnCreateChanged = ofs.Checkbox('UI', config.ShowUIOnCreate)
		ofs.SameLine()
		if ofs.Button("Hide") then
			getVirtualActions():removeAllVirtualActionsInTimelime('gui')
		end
		ofs.SameLine()
		if ofs.Button("Show") then
			updateVirtualPoints()
		end
		ofs.SameLine()
		if ofs.Button("Delete") then
			getVirtualActions():unvirtualizeActionsBefore('gui', player.CurrentTime())
			getVirtualActions():deleteAllVirtualActions('gui')
		end
		
		if ofs.Button("Go back to start") then
			binding.go_back_to_start()
		end
		
		ofs.Text('Adjust:')
		config.adjustVps.TopPointsOffsetReset, resetTopChanged = ofs.Checkbox("R##TPO", config.adjustVps.TopPointsOffsetReset)
		ofs.SameLine()
		config.TopPointsOffset, topChanged = ofs.InputInt("Top Points Offset", config.TopPointsOffset, 1)
		ofs.SameLine()
		if ofs.Button("0##TPO0") then
			config.TopPointsOffset = 0
			topChanged = true
		end
		
		config.adjustVps.BottomPointsOffsetReset, resetBottomChanged = ofs.Checkbox("R##BPO", config.adjustVps.BottomPointsOffsetReset)
		ofs.SameLine()
		config.BottomPointsOffset, bottomChanged = ofs.InputInt("Bottom Points Offset", config.BottomPointsOffset, 1)
		ofs.SameLine()
		if ofs.Button("0##BPO0") then
			config.BottomPointsOffset = 0
			bottomChanged = true
		end

		config.adjustVps.MinimumPositionReset, resetMinChanged = ofs.Checkbox("R##MinP", config.adjustVps.MinimumPositionReset)
		ofs.SameLine()
		config.MinimumPosition, minChanged = ofs.InputInt("Min Pos", config.MinimumPosition, 5)
		ofs.SameLine()
		if ofs.Button("0##MP0") then
			config.MinimumPosition = 0
			minChanged = true
		end

		config.adjustVps.MaximumPositionReset, resetMaxChanged = ofs.Checkbox("R##MaxP", config.adjustVps.MaximumPositionReset)
		ofs.SameLine()
		config.MaximumPosition, maxChanged = ofs.InputInt("Max Pos", config.MaximumPosition, 5)	
		ofs.SameLine()
		if ofs.Button("100##MP100") then
			config.MaximumPosition = 100
			maxChanged = true
		end

		config.adjustVps.AmplitudeCenterReset, resetAmplitudeChanged = ofs.Checkbox("R##CP", config.adjustVps.AmplitudeCenterReset)
		ofs.SameLine()
		config.AmplitudeCenter, amplitudeChanged = ofs.InputInt("Center Pos %", config.AmplitudeCenter, 10)
		ofs.SameLine()
		if ofs.Button("0##AC0") then
			config.AmplitudeCenter = 0
			amplitudeChanged = true
		end
		ofs.SameLine()
		if ofs.Button("50##AC50") then
			config.AmplitudeCenter = 50
			amplitudeChanged = true
		end
		ofs.SameLine()
		if ofs.Button("100##AC100") then
			config.AmplitudeCenter = 100
			amplitudeChanged = true
		end
		config.adjustVps.MinimumPercentageFilledReset, resetPercentageChanged = ofs.Checkbox("R##MF", config.adjustVps.MinimumPercentageFilledReset)
		ofs.SameLine()
		config.MinimumPercentageFilled, percentageChanged = ofs.InputInt("Min % filled", config.MinimumPercentageFilled, 10)
		ofs.SameLine()
		if ofs.Button("0##MPF0") then
			config.MinimumPercentageFilled = 0
			percentageChanged = true
		end	
		
		config.adjustVps.ExtraAmplitudePercentageReset, resetExtraAmplitudeChanged = ofs.Checkbox("R##Ext", config.adjustVps.ExtraAmplitudePercentageReset)
		ofs.SameLine()
		config.ExtraAmplitudePercentage, extraAmplitudeChanged = ofs.InputInt("Extra %", config.ExtraAmplitudePercentage, 10)		
		ofs.SameLine()
		if ofs.Button("0##EAP0") then
			config.ExtraAmplitudePercentage = 0
			extraAmplitudeChanged = true
		end

		local isDiffFromDefault = config.adjustVps.TopPointsOffset ~= config.TopPointsOffset or
		    config.adjustVps.BottomPointsOffset ~= config.BottomPointsOffset or
		    config.adjustVps.MinimumPosition ~= config.MinimumPosition or
		    config.adjustVps.MaximumPosition ~= config.MaximumPosition or
		    config.adjustVps.MinimumPercentageFilled ~= config.MinimumPercentageFilled or
		    config.adjustVps.AmplitudeCenter ~= config.AmplitudeCenter or
		    config.adjustVps.ExtraAmplitudePercentage ~= config.ExtraAmplitudePercentage

		ofs.Text('Default:')
		ofs.BeginDisabled(not isDiffFromDefault)
		ofs.SameLine()
		if ofs.Button("Set") then		
			config.adjustVps.TopPointsOffset 							= config.TopPointsOffset
			config.adjustVps.BottomPointsOffset 						= config.BottomPointsOffset
			config.adjustVps.MinimumPosition 							= config.MinimumPosition
			config.adjustVps.MaximumPosition 							= config.MaximumPosition
			config.adjustVps.MinimumPercentageFilled 					= config.MinimumPercentageFilled
			config.adjustVps.AmplitudeCenter 							= config.AmplitudeCenter
			config.adjustVps.ExtraAmplitudePercentage 					= config.ExtraAmplitudePercentage
			saveConfig()
		end
		ofs.SameLine()
		if ofs.Button("Reset") then
			fullResetAdjustConfigToDefault()
			updateVirtualPoints()
		end
		ofs.EndDisabled()
	
		if topChanged or bottomChanged or minChanged or maxChanged or percentageChanged or amplitudeChanged or 	extraAmplitudeChanged then
			updateVirtualPoints()
		end
		ofs.Separator()
	end	

	if not ofs.CollapsingHeader or ofs.CollapsingHeader("Learn from script") then
		config.shared.LearningDurationInSeconds, changed00 = ofs.InputInt("Script Duration (sec)", config.shared.LearningDurationInSeconds, 2)
		config.shared.LearningDurationInSeconds = clamp(config.shared.LearningDurationInSeconds, 0, 1000)	
		config.shared.DefaultActivityFilter, changed01 = ofs.InputInt("Default Activity Filter", config.shared.DefaultActivityFilter, 5)
		config.shared.DefaultActivityFilter = clamp(config.shared.DefaultActivityFilter, 0, 100)
		config.shared.DefaultQualityFilter, changed02 = ofs.InputInt("Default Quality Filter", config.shared.DefaultQualityFilter, 5)
		config.shared.DefaultQualityFilter = clamp(config.shared.DefaultQualityFilter, 50, 100)
		local increment = 1
		if config.shared.DefaultMinimumPercentageFilter <= 2 then
			increment = 0.1
		end
		config.shared.DefaultMinimumPercentageFilter, changed03 = ofs.Input("Default Min % Filter", config.shared.DefaultMinimumPercentageFilter, increment)
		config.shared.DefaultMinimumPercentageFilter = clamp(config.shared.DefaultMinimumPercentageFilter, 0, 100)
		ofs.Separator()
	end	
	if not ofs.CollapsingHeader or ofs.CollapsingHeader("Actions generation") then
		config.shared.MaximumDurationToGenerateInSeconds, changed04 = ofs.InputInt("Maximum Generation (sec)", config.shared.MaximumDurationToGenerateInSeconds, 10)
		config.shared.MaximumDurationToGenerateInSeconds = clamp(config.shared.MaximumDurationToGenerateInSeconds, 20, 100000)
	
		config.shared.MaximumNbStrokesDetectedPerSecond, changed05 = ofs.Input("Maximum Strokes per sec", config.shared.MaximumNbStrokesDetectedPerSecond, 0.5)
		config.shared.MaximumNbStrokesDetectedPerSecond = clamp(config.shared.MaximumNbStrokesDetectedPerSecond, 1.0, 5.0)
		ofs.Separator()
	end
	if not ofs.CollapsingHeader or ofs.CollapsingHeader("Others config") then
		config.shared.TopMostUI, topMostUIChanged = ofs.Checkbox('TopMost UI', config.shared.TopMostUI)
		config.shared.MaximumMemoryUsageInMB, changed06 = ofs.InputInt("Maximum Memory Usage (MB)", config.shared.MaximumMemoryUsageInMB, 50)
		config.shared.MaximumMemoryUsageInMB = clamp(config.shared.MaximumMemoryUsageInMB, 0, 100000)
		config.EnableLogs, changed07 = ofs.Checkbox("Enable Logs", config.EnableLogs)		
		ofs.Separator()
	end
	
	if showUIOnCreateChanged or 
		changed00 or 
		changed01 or 
		changed02 or 
		changed03 or 
		changed04 or 
		changed05 or 
		changed06 or 
		changed07 or 
		topMostUIChanged or 
		resetTopChanged or 
		resetBottomChanged or 
		resetMinChanged or 
		resetMaxChanged or 
		resetPercentageChanged or 
		resetAmplitudeChanged or 
		resetExtraAmplitudeChanged then
		
		saveConfig()
	end
end
