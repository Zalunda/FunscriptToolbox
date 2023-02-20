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

function virtual_actions:init(actions, frameDurationInMs)
	self.GeneratedActionsOriginal = {}
	
	if #actions > 0 then
		script = ofs.Script(scriptIdx)
		local zoneStartAction = script:closestActionBefore(actions[1].at - 0.001)
		local zoneEndAction = script:closestActionAfter(actions[1].at + 0.001)
		local zoneStart = zoneStartAction and zoneStartAction.at or 0
		local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000

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
				isTop = isTop
			}

			if new_action.at > zoneStart and new_action.at < zoneEnd then
				table.insert(self.GeneratedActionsOriginal, new_action)
			end

			previousIsTop = isTop
			previousAction = action
		end
		
		self.FrameDurationInSec = frameDurationInMs / 1000
		self:update()
	end
end

function virtual_actions:removeVirtualActionsInTimelime()

	script = ofs.Script(scriptIdx)
	if self.ActionsInTimeline and #self.ActionsInTimeline > 0 then
		local firstAction = self.ActionsInTimeline[1]
		local lastAction = self.ActionsInTimeline[#self.ActionsInTimeline]
		
		for idx, action in ipairs(script.actions) do
			if action.at >= firstAction.at and action.at <= lastAction.at then
				script:markForRemoval(idx)
			end
		end
		script:removeMarked()
		script:commit()
	end
end

function virtual_actions:update()

	self:removeVirtualActionsInTimelime()

	if self.GeneratedActionsOriginal and #self.GeneratedActionsOriginal > 0 then

		script = ofs.Script(scriptIdx)

		local zoneStartAction = script:closestActionBefore(self.GeneratedActionsOriginal[1].at - 0.001)
		local zoneEndAction = script:closestActionAfter(self.GeneratedActionsOriginal[1].at + 0.001)
		local zoneStart = zoneStartAction and zoneStartAction.at or 0
		local zoneEnd = zoneEndAction and zoneEndAction.at or 10000000
	
		self.ActionsInTimeline = {}

		-- Add the points, ajusting the at, if needed
		for i, action in ipairs(self.GeneratedActionsOriginal) do
			-- Ajust at
			local newAt = action.at
			if action.isTop then
				newAt = action.at + self.FrameDurationInSec * self.Config.TopPointsOverride
			else
				newAt = action.at + self.FrameDurationInSec * self.Config.BottomPointsOverride
			end
			
			local new_action = Action.new(newAt, action.pos, false)
			table.insert(self.ActionsInTimeline, new_action)
		end

		-- Make sure that point are still in order and don't overlap (because of the top/bottom override)
		for i = 2, #self.ActionsInTimeline do
			local indexToFix = i
			while indexToFix > 1 and self.ActionsInTimeline[indexToFix - 1].at >= self.ActionsInTimeline[indexToFix].at do
				self.ActionsInTimeline[indexToFix - 1].at = self.ActionsInTimeline[indexToFix].at - self.FrameDurationInSec
				indexToFix = indexToFix - 1
			end
		end
			
		-- Adjust position, according to the user preference
		local ratio = (self.Config.MaximumPosition - self.Config.MinimumPosition) / 100
		local minAmplitude = self.Config.MinimumPercentageFilled * ratio
		local amplitudeRange = (100 - self.Config.MinimumPercentageFilled) * ratio
		local centerPosition = self.Config.MinimumPosition + self.Config.AmplitudeCenter * ratio

		local previousPos = self.ActionsInTimeline[1].pos
		for i = 2, #self.ActionsInTimeline do
			local currentPos = self.ActionsInTimeline[i].pos;
			
			local distance = math.abs(previousPos - currentPos)
			if distance > 0 then
				local finalAmplitude = minAmplitude + distance * amplitudeRange / 100
				local minPosition = math.max(self.Config.MinimumPosition, centerPosition - finalAmplitude / 2)
				local maxPosition = minPosition + finalAmplitude
				if maxPosition > self.Config.MaximumPosition then
					local diff = maxPosition - self.Config.MaximumPosition
					maxPosition = maxPosition - diff
					minPosition = minPosition - diff
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
		
		-- Remove the item that are would outside our 'zone' (i.e. overlap existing actions)
		for i = #self.ActionsInTimeline, 1, -1 do
			local action = self.ActionsInTimeline[i]
			if action.at <= zoneStart or action.at >= zoneEnd then
				table.remove(self.ActionsInTimeline, i)
			end
		end

		-- Finally, add the points to OFS timeline
		for i, action in ipairs(self.ActionsInTimeline) do
			script.actions:add(action)
		end

		script:commit()
	end
end

function virtual_actions:getNumberOfActions()
	return #self.ActionsInTimeline
end

function virtual_actions:removeActionsBefore(time)
 	if self.GeneratedActionsOriginal then
 		while #self.GeneratedActionsOriginal > 0 and self.GeneratedActionsOriginal[1].at < time do
 			table.remove(self.GeneratedActionsOriginal, 1)
 		end
 	end
 	if self.ActionsInTimeline then
 		while #self.ActionsInTimeline > 0 and self.ActionsInTimeline[1].at < time do
 			table.remove(self.ActionsInTimeline, 1)
 		end
 	end
end

return virtual_actions
