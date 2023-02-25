-- FunscriptToolbox.MotionVectors LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"
virtual_actions = require "virtual_actions"

-- global var
FTMVSFullPath = "[[FunscriptToolboxExePathInLuaFormat]]"
PluginVersion = "[[PluginVersion]]"
configFullPath = ofs.ExtensionDir() .. "\\config.json"

config = {}
sharedConfig = {}
connection = nil
virtualActions = {}
lastVideoFullPath = nil
lastMvsFullPath = nil

function init()
    print("Plugin Version:", PluginVersion)

	loadOrCreateConfig()
	connection = server_connection:new(FTMVSFullPath, config.EnableLogs)
	
	sendCheckVersionRequest()
end

function loadOrCreateConfig()

	local f = io.open(configFullPath)
	local loaded_config
    if f then
		local content = f:read("*a")
		f:close()
		loaded_config = json.decode(content)
	else
		loaded_config = { config = {}, sharedConfig = {}}
    end

	config.EnableLogs 								= loaded_config.config.EnableLogs == nil and true or loaded_config.config.EnableLogs
	config.TopPointsOffset 							= loaded_config.config.TopPointsOffset 							or 0
	config.BottomPointsOffset 						= loaded_config.config.BottomPointsOffset 						or 0
	config.MinimumPosition 							= loaded_config.config.MinimumPosition 							or 0
	config.MaximumPosition 							= loaded_config.config.MaximumPosition 							or 100
	config.MinimumPercentageFilled 					= loaded_config.config.MinimumPercentageFilled 					or 0
	config.AmplitudeCenter 							= loaded_config.config.AmplitudeCenter 							or 50
	config.ExtraAmplitudePercentage 				= loaded_config.config.ExtraAmplitudePercentage 				or 0
	config.ShowUIOnCreate							= loaded_config.config.ShowUIOnCreate == nil and true or loaded_config.config.ShowUIOnCreate

	sharedConfig.MaximumMemoryUsageInMB 			= loaded_config.sharedConfig.MaximumMemoryUsageInMB 			or 1000
	sharedConfig.LearningDurationInSeconds 			= loaded_config.sharedConfig.LearningDurationInSeconds 			or 10
	sharedConfig.DefaultActivityFilter 				= loaded_config.sharedConfig.DefaultActivityFilter 				or 60
	sharedConfig.DefaultQualityFilter 				= loaded_config.sharedConfig.DefaultQualityFilter 				or 90
	sharedConfig.DefaultMinimumPercentageFilter 	= loaded_config.sharedConfig.DefaultMinimumPercentageFilter 	or 5
	sharedConfig.MaximumDurationToGenerateInSeconds = loaded_config.sharedConfig.MaximumDurationToGenerateInSeconds	or 120
	sharedConfig.MaximumNbStrokesDetectedPerSecond 	= loaded_config.sharedConfig.MaximumNbStrokesDetectedPerSecond  or 3.0
	sharedConfig.TopMostUI							= loaded_config.sharedConfig.TopMostUI == nil and true or loaded_config.sharedConfig.TopMostUI
end

function saveConfig()
	local encoded_data = json.encode({config = config, sharedConfig = sharedConfig})
 	local requestFile = io.open(configFullPath , "w")
 	requestFile:write(encoded_data)
 	requestFile:close()
end


function update(delta)
	connection:processResponses()
end

function scriptChange(scriptIdx)
	getVirtualActions(scriptIdx):removeActionsBefore(player.CurrentTime())
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
		SharedConfig = sharedConfig
	}
end

function sendCreateRulesRequest(showUI)
	getVirtualActions():removeVirtualActionsInTimelime()

	local script = ofs.Script(ofs.ActiveIdx())
	local firstAction, indexBefore = script:closestActionAfter(player.CurrentTime() - sharedConfig.LearningDurationInSeconds)
	local lastAction, indexAfter = script:closestActionBefore(player.CurrentTime() + 0.001)
	local nextAction = script:closestActionAfter(player.CurrentTime() + 0.001);

 	local request = createRequest("CreateRulesPluginRequest");
	
    local videoFullPath = player.CurrentVideo()			
	local mvsFullPath = nil
	
	if lastVideoFullPath == videoFullPath then
		mvsFullPath = lastMvsFullPath
	else
		print('Trying to find .mvs file from videoFullPath...')
		local combinedPart = nil
		for part in string.gmatch(videoFullPath, "[^.]+") do
			combinedPart = combinedPart and combinedPart .. "." .. part or part
			potentialMvsFullPath = combinedPart .. ".mvs"
			local file = io.open(potentialMvsFullPath, "r")
			if file then
				print('   FOUND ' .. potentialMvsFullPath)
				io.close(file)
				mvsFullPath = potentialMvsFullPath
			else
				print('   ' .. potentialMvsFullPath)
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
		request.Actions = {}
		request.SelectedActions = {}

		if nextAction then
			request.DurationToGenerateInSeconds = math.min(sharedConfig.MaximumDurationToGenerateInSeconds, nextAction.at - player.CurrentTime())
		else
			request.DurationToGenerateInSeconds = sharedConfig.MaximumDurationToGenerateInSeconds
		end
		request.ShowUI = showUI
			
		for _, action in ipairs(script.actions) do
			if (action.at >= firstAction.at and action.at <= lastAction.at) then
				table.insert(request.Actions, { at = math.floor(action.at * 1000 + 0.5), pos = action.pos })
			end
		end
		
		if script:hasSelection() then
			for _, indice in ipairs(script:selectedIndices()) do
				print(indice)
			
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

function handleCreateRulesResponse(response)
	if response.Actions then
		local scriptIdx = ofs.ActiveIdx() -- todo save in request/response
		local va = getVirtualActions(scriptIdx)
		va:init(response.Actions, response.FrameDurationInMs)
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
		print(updateVersionMessage)
	end
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
function updateVirtualPoints()
	config.TopPointsOffset = clamp(config.TopPointsOffset, -10, 10)
	config.BottomPointsOffset = clamp(config.BottomPointsOffset, -10, 10)
	config.MinimumPosition = clamp(config.MinimumPosition, 0, 95)
	config.MaximumPosition = clamp(config.MaximumPosition, config.MinimumPosition + 5, 100)
	config.MinimumPercentageFilled = clamp(config.MinimumPercentageFilled, 0, 100)
	config.AmplitudeCenter = clamp(config.AmplitudeCenter, 0, 100)
	config.ExtraAmplitudePercentage = clamp(config.ExtraAmplitudePercentage, 0, 1000)
	getVirtualActions():update(player.CurrentTime())
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
    ofs.Text("Connection: " .. connection:getStatus())
    ofs.Text("Virtual Actions: " .. getVirtualActions():getStatus())
	ofs.Separator()
	
	local showUIOnCreateChanged = false
	if ofs.CollapsingHeader("Virtual Actions") then
		if ofs.Button("Create") then			
			sendCreateRulesRequest(config.ShowUIOnCreate)
		end
		ofs.SameLine()
		config.ShowUIOnCreate, showUIOnCreateChanged = ofs.Checkbox('UI', config.ShowUIOnCreate)
		ofs.SameLine()
		if ofs.Button("Hide") then
			getVirtualActions():removeVirtualActionsInTimelime()
		end
		ofs.SameLine()
		if ofs.Button("Show") then
			updateVirtualPoints()
		end
		ofs.SameLine()
		if ofs.Button("Delete") then
			getVirtualActions():deleteVirtualActions()
		end
		
		if ofs.Button("Go back to start") then
			binding.go_back_to_start()
		end
		
		ofs.Text('Adjust:')
		config.TopPointsOffset, topChanged = ofs.InputInt("Top Points Offset", config.TopPointsOffset, 1)
		ofs.SameLine()
		if ofs.Button("0##TPO0") then
			config.TopPointsOffset = 0
			topChanged = true
		end
		
		config.BottomPointsOffset, bottomChanged = ofs.InputInt("Bottom Points Offset", config.BottomPointsOffset, 1)
		ofs.SameLine()
		if ofs.Button("0##BPO0") then
			config.BottomPointsOffset = 0
			bottomChanged = true
		end

		config.MinimumPosition, minChanged = ofs.InputInt("Min Pos", config.MinimumPosition, 5)
		ofs.SameLine()
		if ofs.Button("0##MP0") then
			config.MinimumPosition = 0
			minChanged = true
		end

		config.MaximumPosition, maxChanged = ofs.InputInt("Max Pos", config.MaximumPosition, 5)	
		ofs.SameLine()
		if ofs.Button("100##MP100") then
			config.MaximumPosition = 100
			maxChanged = true
		end

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
		config.MinimumPercentageFilled, percentageChanged = ofs.InputInt("Min % filled", config.MinimumPercentageFilled, 10)
		ofs.SameLine()
		if ofs.Button("0##MPF0") then
			config.MinimumPercentageFilled = 0
			percentageChanged = true
		end	
		
		config.ExtraAmplitudePercentage, extraAmplitudeChanged = ofs.InputInt("Extra %", config.ExtraAmplitudePercentage, 10)		
		ofs.SameLine()
		if ofs.Button("0##EAP0") then
			config.ExtraAmplitudePercentage = 0
			extraAmplitudeChanged = true
		end

		if topChanged or bottomChanged or minChanged or maxChanged or percentageChanged or amplitudeChanged or extraAmplitudeChanged then
			updateVirtualPoints()
		end
	end	

	if ofs.CollapsingHeader("Learn from script") then
		sharedConfig.LearningDurationInSeconds, changed00 = ofs.InputInt("Script Duration (sec)", sharedConfig.LearningDurationInSeconds, 2)
		sharedConfig.LearningDurationInSeconds = clamp(sharedConfig.LearningDurationInSeconds, 0, 1000)	
		sharedConfig.DefaultActivityFilter, changed01 = ofs.InputInt("Default Activity Filter", sharedConfig.DefaultActivityFilter, 5)
		sharedConfig.DefaultActivityFilter = clamp(sharedConfig.DefaultActivityFilter, 0, 100)
		sharedConfig.DefaultQualityFilter, changed02 = ofs.InputInt("Default Quality Filter", sharedConfig.DefaultQualityFilter, 5)
		sharedConfig.DefaultQualityFilter = clamp(sharedConfig.DefaultQualityFilter, 50, 100)
		sharedConfig.DefaultMinimumPercentageFilter, changed03 = ofs.InputInt("Default Min % Filter", sharedConfig.DefaultMinimumPercentageFilter, 1)
		sharedConfig.DefaultMinimumPercentageFilter = clamp(sharedConfig.DefaultMinimumPercentageFilter, 0, 100)
	end	
	if ofs.CollapsingHeader("Actions generation") then
		sharedConfig.MaximumDurationToGenerateInSeconds, changed04 = ofs.InputInt("Maximum Generation (sec)", sharedConfig.MaximumDurationToGenerateInSeconds, 10)
		sharedConfig.MaximumDurationToGenerateInSeconds = clamp(sharedConfig.MaximumDurationToGenerateInSeconds, 20, 100000)
	
		sharedConfig.MaximumNbStrokesDetectedPerSecond, changed05 = ofs.Input("Maximum Strokes per sec", sharedConfig.MaximumNbStrokesDetectedPerSecond, 0.5)
		sharedConfig.MaximumNbStrokesDetectedPerSecond = clamp(sharedConfig.MaximumNbStrokesDetectedPerSecond, 1.0, 5.0)
	end
	if ofs.CollapsingHeader("Others config") then
		sharedConfig.TopMostUI, topMostUIChanged = ofs.Checkbox('TopMost UI', sharedConfig.TopMostUI)
		sharedConfig.MaximumMemoryUsageInMB, changed06 = ofs.InputInt("Maximum Memory Usage (MB)", sharedConfig.MaximumMemoryUsageInMB, 50)
		sharedConfig.MaximumMemoryUsageInMB = clamp(sharedConfig.MaximumMemoryUsageInMB, 0, 100000)
		config.EnableLogs, changed07 = ofs.Checkbox("Enable Logs", config.EnableLogs)		
	end
	
	if showUIOnCreateChanged or changed00 or changed01 or changed02 or changed03 or changed04 or changed05 or changed06 or changed07 or topMostUIChanged then
		saveConfig()
	end
end
