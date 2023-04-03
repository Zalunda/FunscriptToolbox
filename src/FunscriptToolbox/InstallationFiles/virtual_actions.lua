-- FunscriptToolbox.MotionVectors LUA virtual_actions
local virtual_actions = {}

function virtual_actions:new(scriptIdx, config)
	local o = { 
		ScriptIdx = scriptIdx,		
		Config = config,
		FrameDurationInSec = 167,
		GeneratedActionsOriginal = {},
		ActionsInTimeline = nil
	}
	setmetatable(o, {__index = virtual_actions})
	return o
end

function virtual_actions:init(userAction, actions, frameDurationInMs)
	self.GeneratedActionsOriginal = {}
	self:removeAllVirtualActionsInTimelime(userAction .. ' init')
	if #actions > 0 then	
		local script = ofs.Script(self.ScriptIdx)
		local zoneStartAction = script:closestActionBefore(actions[1].at / 1000 - 0.001)
		local zoneEndAction = script:closestActionAfter(actions[1].at / 1000 + self.FrameDurationInSec / 2)
		local zoneStart = zoneStartAction and zoneStartAction.at or 0
		local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000
		if config.EnableLogs then printWithTime(userAction, 'init', 'zoneStart', getFormattedTime(zoneStart)) end
		if config.EnableLogs then printWithTime(userAction, 'init', 'zoneEnd', getFormattedTime(zoneEnd)) end

		local logsInfo = ''
		local previousAction = nil
		local previousIsTop = nil
		for i, action in ipairs(actions) do	
			local isTop
			if not previousAction then
				isTop = action.pos > actions[i + 1].pos
			elseif action.pos == previousAction.pos then
				isTop = previousIsTop
			else
				isTop = action.pos > previousAction.pos
			end		
			local new_action = { 
				at = action.at / 1000.0,
				pos = action.pos,
				isTop = isTop,
				enabled = true
			}

			if new_action.at > zoneStart and new_action.at < zoneEnd then
				table.insert(self.GeneratedActionsOriginal, new_action)
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(new_action.at) .. ', ' end
			else
				if config.EnableLogs then logsInfo = logsInfo .. 'SKIP:' .. getFormattedTime(new_action.at) .. ', ' end
			end

			previousIsTop = isTop
			previousAction = action
		end
		if config.EnableLogs then printWithTime(userAction, 'init.insert', #self.GeneratedActionsOriginal, logsInfo) end
		
		self.FrameDurationInSec = frameDurationInMs / 1000
		self:update(userAction .. ' init')
	end
end

function virtual_actions:removeAllVirtualActionsInTimelime(userAction)

	local script = ofs.Script(self.ScriptIdx)
	if self.ActionsInTimeline and #self.ActionsInTimeline > 0 then		
		local firstAction = self.ActionsInTimeline[1]
		local lastAction = self.ActionsInTimeline[#self.ActionsInTimeline]
		
		local logsInfo = ''
		for idx, action in ipairs(script.actions) do
			if action.at >= firstAction.at and action.at <= lastAction.at then
				script:markForRemoval(idx)
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
			end
		end
		script:removeMarked()
		script:commit()
		
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'removeAllVirtualActionsInTimelime', #self.ActionsInTimeline .. '=>0', logsInfo) end
	end
end

function virtual_actions:unvirtualizeActionsBefore(userAction, time)
	
 	if self.GeneratedActionsOriginal then
		local logsInfo = ''
		for i, action in ipairs(self.GeneratedActionsOriginal) do
			if action.at <= time and action.enabled then
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
				action.enabled = false
			end
		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'unvirtualizeActionsBefore.GeneratedActionsOriginal', #self.GeneratedActionsOriginal, logsInfo) end
 	end

 	if self.ActionsInTimeline then
		local logsInfo = ''
 		while #self.ActionsInTimeline > 0 and self.ActionsInTimeline[1].at <= time do
			if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(self.ActionsInTimeline[1].at) .. ', ' end
 			table.remove(self.ActionsInTimeline, 1)
 		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'unvirtualizeActionsBefore.ActionsInTimeline', #self.ActionsInTimeline, logsInfo) end
 	end
end

function virtual_actions:deleteAllVirtualActions(userAction)

	if config.EnableLogs then printWithTime(userAction, 'deleteAllVirtualActions') end
	self:removeAllVirtualActionsInTimelime(userAction .. ' deleteAllVirtualActions')
	self.ActionsInTimeline = {}
	self.GeneratedActionsOriginal = {}
end

function virtual_actions:getStartTime()
	if #self.ActionsInTimeline > 0 then
		return self.ActionsInTimeline[1].at
	else
		return nil
	end
end

function virtual_actions:update(userAction)

	self:removeAllVirtualActionsInTimelime(userAction .. ' update')

	if self.GeneratedActionsOriginal and #self.GeneratedActionsOriginal > 0 then

		local script = ofs.Script(self.ScriptIdx)

		self.ActionsInTimeline = {}

		-- 1. Add the points, ajusting the at with top/bottom offset
		local zoneStartAction = nil
		local zoneEndAction = nil
		for i, action in ipairs(self.GeneratedActionsOriginal) do	
			if action.enabled then 
				if not zoneStartAction then
					zoneStartAction = script:closestAction(action.at)
					zoneEndAction = script:closestActionAfter(action.at + self.FrameDurationInSec / 2)
				end
			
				local newAt = action.at
				if action.isTop then
					newAt = action.at + self.FrameDurationInSec * self.Config.TopPointsOffset
				else
					newAt = action.at + self.FrameDurationInSec * self.Config.BottomPointsOffset
				end
				
				local new_action = Action.new(newAt, action.pos, false)
				table.insert(self.ActionsInTimeline, new_action)
			end
		end	
		local zoneStart = zoneStartAction and zoneStartAction.at or 0
		local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000
		if config.EnableLogs then printWithTime(userAction, 'update', 'zoneStart', getFormattedTime(zoneStart)) end
		if config.EnableLogs then printWithTime(userAction, 'update', 'zoneEnd', getFormattedTime(zoneEnd)) end

		-- 2. Make sure that actions are still in order and don't overlap (because of the top/bottom offset).
		for i = 2, #self.ActionsInTimeline do
			local indexToFix = i
			while indexToFix > 1 and self.ActionsInTimeline[indexToFix - 1].at >= self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec / 2 do
				if config.EnableLogs then printWithTime(userAction, 'update', 'fixing', self.ActionsInTimeline[indexToFix - 1].at, 'to', self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec) end
				self.ActionsInTimeline[indexToFix - 1].at = self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec
				indexToFix = indexToFix - 1
			end
		end
			
		-- Adjust position, according to the user preference
		local totalAmplitude = self.Config.MaximumPosition - self.Config.MinimumPosition
		local ratio = totalAmplitude / 100
		local minAmplitude = self.Config.MinimumPercentageFilled * ratio		
		local amplitudeRange = (100 - self.Config.MinimumPercentageFilled) * ratio

		local previousPos = self.ActionsInTimeline[1].pos
		for i = 2, #self.ActionsInTimeline do
			local currentPos = self.ActionsInTimeline[i].pos;
			
			local distance = math.abs(previousPos - currentPos)
			if distance > 0 then
				local finalAmplitude = (minAmplitude + distance * amplitudeRange / 100) * (1 + self.Config.ExtraAmplitudePercentage / 100)
				local unusedAmplitude = totalAmplitude - finalAmplitude
				minPosition = self.Config.MinimumPosition
				maxPosition = self.Config.MaximumPosition
				if unusedAmplitude > 0 then									
					minPosition = minPosition + unusedAmplitude * self.Config.AmplitudeCenter / 100
					maxPosition = minPosition + finalAmplitude
				end
				
				if previousPos < currentPos then
				 	self.ActionsInTimeline[i - 1].pos = minPosition
				 	self.ActionsInTimeline[i].pos = maxPosition
				else
				 	self.ActionsInTimeline[i - 1].pos = maxPosition
				 	self.ActionsInTimeline[i].pos = minPosition
				end
				previousPos = currentPos
			end
		end
		
		-- Remove the item that are would outside our 'zone' (i.e. they might overlap existing actions)
		local logsInfo = ''
		for i = #self.ActionsInTimeline, 1, -1 do
			local action = self.ActionsInTimeline[i]
			local closestAction = script:closestAction(action.at)
			if action.at <= zoneStart or action.at >= zoneEnd then
				if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-zone] ' .. getFormattedTime(action.at) .. ', ' end
				table.remove(self.ActionsInTimeline, i)
			elseif closestAction and math.abs(closestAction.at - action.at) < self.FrameDurationInSec / 2 then
				if config.EnableLogs then logsInfo = logsInfo .. '[SKIP-too-close] ' .. getFormattedTime(action.at) .. ', ' end
			    table.remove(self.ActionsInTimeline, i)
			else
				if config.EnableLogs then logsInfo = logsInfo .. getFormattedTime(action.at) .. ', ' end
			end
		end
		if config.EnableLogs and #logsInfo > 0 then printWithTime(userAction, 'update', #self.ActionsInTimeline, logsInfo) end

		-- self:debugSameTimestamp(zoneStart, zoneEnd)

		-- Finally, add the points to OFS timeline
		for i, action in ipairs(self.ActionsInTimeline) do
			script.actions:add(action)
		end

		script:commit()
	end
end

function virtual_actions:debugSameTimestamp(zoneStart, zoneEnd)
	
	printWithTime('---debugSameTimestamp---')
	printWithTime('zoneStart ' .. zoneStart)
	printWithTime('zoneEnd ' .. zoneEnd)
	local script = ofs.Script(self.ScriptIdx)
	for i, action in ipairs(self.ActionsInTimeline) do		
		local closest = script:closestAction(action.at)
		if closest and action.at == closest.at then
			printWithTime('bug at ' .. action.at .. ' (' .. i .. ')')
		end
	end
	printWithTime('------------------------')
end

function virtual_actions:getStatus(time)

	if self.ActionsInTimeline and #self.ActionsInTimeline > 0 then
		local firstAction = self.ActionsInTimeline[1]
		local lastAction = self.ActionsInTimeline[#self.ActionsInTimeline]
		return #self.ActionsInTimeline .. ' actions, ' .. lastAction.at - firstAction.at .. ' secs', nil
	else
		local script = ofs.Script(self.ScriptIdx)
		local closestAction = script:closestActionAfter(time)
		if closestAction then
			return 'empty, next action in ' .. closestAction.at - time .. ' secs', nil
		else
			return 'empty', nil
		end
	end

end

return virtual_actions
