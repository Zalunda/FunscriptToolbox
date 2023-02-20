-- FunscriptToolbox.MotionVectors LUA Wrappper Version 1.0.0
json = require "json"
server_connection = require "server_connection"
virtual_actions = require "virtual_actions"

-- global var
FTMVSFullPath = "C:\\Partage\\Medias\\Sources\\GitHub.Mine\\FunscriptToolbox\\src\\FunscriptToolbox\\bin\\Release\\FunscriptToolbox.exe" --TODO Replace with  [[FunscriptToolboxExePathInLuaFormat]]
status = "FunscriptToolbox.MotionVectors not running"
updateCounter = 0
scriptIdx = 1

config = {
	PluginVersion = "1.0.0", -- TODO Replace with "[[PluginVersion]]",
	EnableLogs = false,
	MaximumDurationToGenerateInSeconds = 200,
	TopPointsOverride = 0,
	BottomPointsOverride = 0,
	MinimumPosition = 0,
	MaximumPosition = 100,
	MinimumPercentageFilled = 0,
	AmplitudeCenter = 50,
}

sharedConfig = {
	MaximumMemoryUsageInMB = 1000,
	DefaultLearningDurationInSeconds = 10,
	MaximumLearningDurationInSeconds = 60,
	DefaultActivityFilter = 60,
	DefaultQualityFilter = 75,
	MaximumNbStrokesDetectedPerSecond = 3.0
}

connection = nil

function init()
    print("Plugin Version:", config.PluginVersion)
	-- TODO cleanup old communicationChannel
	
	connection = server_connection:new(FTMVSFullPath, config.enableLogs)
	virtualActions = {}
	-- Check ServerLastestPluginVersion vs PluginVersion

    status = "FunscriptToolbox.MotionVectors running"
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

    local videoFullPath = player.CurrentVideo()
	local mvsFullPath = videoFullPath:gsub("-visual.mp4", "")
 	return {
		["$type"] = service,
		VideoFullPath = videoFullPath,
		CurrentVideoTime = math.floor(player.CurrentTime() * 1000),
		MvsFullPath = mvsFullPath,
		SharedConfig = sharedConfig
	}
end

function binding.start_funscripttoolbox_motionvectors()

	getVirtualActions():removeVirtualActionsInTimelime()

	scriptIdx = ofs.ActiveIdx()
	script = ofs.Script(scriptIdx)
	local firstAction, indexBefore = script:closestActionAfter(player.CurrentTime() - sharedConfig.MaximumLearningDurationInSeconds)
	local lastAction, indexAfter = script:closestActionBefore(player.CurrentTime() + 0.1)
	local nextAction = script:closestActionAfter(player.CurrentTime() + 0.001);

 	local request = createRequest("CreateRulesFromScriptActionsPluginRequest");
	request.Actions = {}
	request.SelectedActions = {}

	if nextAction then
		request.DurationToGenerateInSeconds = math.min(config.MaximumDurationToGenerateInSeconds, nextAction.at - player.CurrentTime())
	else
		request.DurationToGenerateInSeconds = config.MaximumDurationToGenerateInSeconds
	end
	request.ShowUI = true

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

	connection:sendRequest(request, handleCreateRulesFromScriptActionsResponse)
end

function handleCreateRulesFromScriptActionsResponse(response)

	scriptIdx = ofs.ActiveIdx() -- todo save in request/response
	local va = getVirtualActions(scriptIdx)	
	va:init(response.Actions, response.FrameDurationInMs)
end

function update(delta)
    updateCounter = updateCounter + 1
    if math.fmod(updateCounter, 10) == 0 then
		connection:processResponses()
		getVirtualActions():removeActionsBefore(player.CurrentTime())
    end
end

function binding.top_points_move_to_left()
	config.TopPointsOverride = config.TopPointsOverride - 5
	updateVirtualPoints()
end 
function binding.top_points_move_to_right()
	config.TopPointsOverride = config.TopPointsOverride + 5
	updateVirtualPoints()
end 
function binding.bottom_points_move_to_left()
	config.BottomPointsOverride = config.BottomPointsOverride - 5
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
function updateVirtualPoints()
	print('updateVirtualPoints')
	config.MinimumPosition = clamp(config.MinimumPosition, 0, 95)
	config.MaximumPosition = clamp(config.MaximumPosition, config.MinimumPosition + 5, 100)
	config.MinimumPercentageFilled = clamp(config.MinimumPercentageFilled, 0, 100)
	config.AmplitudeCenter = clamp(config.AmplitudeCenter, 0, 100)
	getVirtualActions():update()
end

function gui()
    ofs.Text("Connection Status: " .. connection:GetStatus())
	ofs.Separator()
	
	if ofs.CollapsingHeader("Virtual Points") then
		config.TopPointsOverride, topChanged = ofs.InputInt("Top Points At", config.TopPointsOverride, 1)
		config.BottomPointsOverride, bottomChanged = ofs.InputInt("Bottom Points At", config.BottomPointsOverride, 1)
		config.MinimumPosition, minChanged = ofs.InputInt("Min Pos", config.MinimumPosition, 5)
		config.MaximumPosition, maxChanged = ofs.InputInt("Max Pos", config.MaximumPosition, 5)	
		config.MinimumPercentageFilled, percentageChanged = ofs.InputInt("Min Pos % filled", config.MinimumPercentageFilled, 10)
		config.AmplitudeCenter, amplitudeChanged = ofs.InputInt("Center Pos %", config.AmplitudeCenter, 10)
		ofs.SameLine()
		if ofs.Button("Top") then
			config.AmplitudeCenter = 100
			amplitudeChanged = true
		end
		ofs.SameLine()
		if ofs.Button("Bottom") then
			config.AmplitudeCenter = 0
			amplitudeChanged = true
		end
		
		if ofs.Button("Reset All") then
			config.TopPointsOverride = 0
			config.BottomPointsOverride = 0
			config.MinimumPosition = 10
			config.MaximumPosition = 90
			config.AmplitudeCenter = 50
			config.MinimumPercentageFilled = 60
			amplitudeChanged = true
		end

		if topChanged or bottomChanged or minChanged or maxChanged or percentageChanged or amplitudeChanged then
			updateVirtualPoints()
		end

		-- TODO Save Preset
	end	

	if ofs.CollapsingHeader("Learn from script") then
		sharedConfig.DefaultLearningDurationInSeconds, _ = ofs.InputInt("Default duration", sharedConfig.DefaultLearningDurationInSeconds, 2)
		sharedConfig.MaximumLearningDurationInSeconds, _ = ofs.InputInt("Maximum duration", sharedConfig.MaximumLearningDurationInSeconds, 10)
		sharedConfig.MaximumLearningDurationInSeconds = clamp(sharedConfig.MaximumLearningDurationInSeconds, 30, 300)
		sharedConfig.DefaultLearningDurationInSeconds = clamp(sharedConfig.DefaultLearningDurationInSeconds, 0, sharedConfig.MaximumLearningDurationInSeconds)
		
		sharedConfig.DefaultActivityFilter, _ = ofs.InputInt("Default Activity Filter", sharedConfig.DefaultActivityFilter, 5)
		sharedConfig.DefaultActivityFilter = clamp(sharedConfig.DefaultActivityFilter, 0, 100)
		sharedConfig.DefaultQualityFilter, _ = ofs.InputInt("Default Quality Filter", sharedConfig.DefaultQualityFilter, 5)
		sharedConfig.DefaultQualityFilter = clamp(sharedConfig.DefaultQualityFilter, 50, 100)
	end	
	if ofs.CollapsingHeader("Actions generation") then
		sharedConfig.MaximumNbStrokesDetectedPerSecond, _ = ofs.Input("Maximum Strokes per sec", sharedConfig.MaximumNbStrokesDetectedPerSecond, 0.5)
		sharedConfig.MaximumNbStrokesDetectedPerSecond = clamp(sharedConfig.MaximumNbStrokesDetectedPerSecond, 1.0, 5.0)
	end
	if ofs.CollapsingHeader("Others config") then
		sharedConfig.MaximumMemoryUsageInMB, _ = ofs.InputInt("Maximum Memory Usage (MB)", sharedConfig.MaximumMemoryUsageInMB, 50)
		sharedConfig.MaximumMemoryUsageInMB = clamp(sharedConfig.MaximumMemoryUsageInMB, 0, 100000)
	end
end
