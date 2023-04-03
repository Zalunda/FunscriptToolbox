-- FunscriptToolbox.MotionVectors LUA server_connection
json = require "json"

local server_connection = {}

function server_connection:new(FTMVSFullPath, enableLogs)
	local o = { 
		FTMVSFullPath = FTMVSFullPath,
		cantFindFTMVSFullPath = false,
		enableLogs = enableLogs,
		serverProcessHandle = nil,
		requests = {},
		lastTimeTaken = 0,
		serverTimeout = 300,
		status = '',
		statusTooltip = nil
	}
	if not fileExist(o.FTMVSFullPath) then
		local message = 'missing ' .. o.FTMVSFullPath
		print(message)
		o.cantFindFTMVSFullPath = true
		o.status = message
		o.statusTooltip = 'If you moved FunscriptToolbox folder,\nRerun --FSTB-Installation.bat to update the path in the plugin.'
	end
	setmetatable(o, {__index = server_connection})

	return o
end

function fileExist(path)
    local file = io.open(path, "r")
    if file then
        file:close()
        return true
    else
        return false
    end
end

function server_connection:getStatus()
	return self.status, self.statusTooltip
end

-- Find an available 'channel' to communicate with the server (in case multiple OFS are running at the same time)
-- The function open a '.lock' file, which is not closed. This will 'reserve' that channel for this process. 
function server_connection:setBaseCommunicationFilePath()

	-- TODO cleanup old communicationChannel

	for id = 1, 1000 do
        self.requestBaseFilePath = ofs.ExtensionDir() .. "\\Channel-" .. id
        self.responseBaseFilePath = ofs.ExtensionDir() .. "\\Responses\\Channel-" .. id

		local f, message, errno = io.open(self.requestBaseFilePath .. ".lock", "w+")
        if not f then
            printWithTime("Channel file already used: " .. id)
            printWithTime("Message: " .. message)
            printWithTime("errno: " .. errno)
        else
			printWithTime('Channel '.. id .. ' locked for this process.')
			f:close() -- FunscriptToolbox will acquire the actual lock
			self.channelCurrentTransactionNumber = 1
			return
        end
	end
end

function server_connection:startServerIfNeeded()

    if self.serverProcessHandle and self.serverProcessHandle:alive() then
        return true
    end
	if self.cantFindFTMVSFullPath then
		printWithTime('Cannot send request when .exe is missing')
		return false
	end

    self:setBaseCommunicationFilePath()
	self.channelCurrentTransactionNumber = 1

    local cmd = self.FTMVSFullPath
    local args = {}
    table.insert(args, "motionvectors.ofspluginserver")
    if self.enableLogs then;
        table.insert(args, "--verbose")
    end
    table.insert(args, "--channelbasefilepath")
    table.insert(args, self.requestBaseFilePath .. "-")
    table.insert(args, "--channellockfilepath")
    table.insert(args, self.requestBaseFilePath .. ".lock")
    table.insert(args, "--timeout")
    table.insert(args, self.serverTimeout)

    printWithTime("cmd: ", cmd)
    printWithTime("args: ", table.unpack(args))

    self.serverProcessHandle = Process.new(cmd, table.unpack(args))
	return true
end

function server_connection:sendRequest(request, callback)

	if self:startServerIfNeeded() then
		self.nextKeepAlive = os.time() + self.serverTimeout / 2	

		local transactionNumber = self.channelCurrentTransactionNumber
		self.channelCurrentTransactionNumber = self.channelCurrentTransactionNumber + 1
	
		local requestFilePath = self.requestBaseFilePath .. '-' .. transactionNumber .. ".json"
		local responseFilePath = self.responseBaseFilePath .. '-'.. transactionNumber .. ".json"
	
		printWithTime('Sending request #' .. transactionNumber)
		local encoded_data = json.encode(request)
		if self.enableLogs then
			printWithTime(encoded_data)
		end
 		local requestFile = io.open(requestFilePath, "w")
 		requestFile:write(encoded_data)
 		requestFile:close()
	
		-- Add item at the start because we remove then in reverse order in processResponses
		table.insert(self.requests, 1, { 
			transactionNumber = transactionNumber,
			startTime = os.clock(),
			request = request,
			responseFilePath = responseFilePath, 
			callback = callback
		})
	end
end

function server_connection:getResponseForRequest(request)

	local f, message, errno = io.open(request.responseFilePath)
    if not f then
        -- print("message: ", message)
        -- print("errno: ", errno)
        return false
    end

	printWithTime('Reading response #' .. request.transactionNumber)
    local content = f:read("*a")
    f:close()
	if self.enableLogs then
		printWithTime(content)
	end
    response_body = json.decode(content)
	os.remove(request.responseFilePath)
	
	self.lastTimeTaken = os.clock() - request.startTime
	
	if response_body.ErrorMessage then
		printWithTime('---------------------------------')
		printWithTime('Error received from the server:')
		printWithTime(response_body.ErrorMessage)		
		printWithTime('---------------------------------')
		self.status = 'ERROR RECEIVED (see tooltip)'
		self.statusTooltip = response_body.ErrorMessage
		
	elseif  request.callback then 
		request.callback(response_body)
	    self.status = 'Last request took ' .. self.lastTimeTaken .. ' seconds'
		self.statusTooltip = nil
	end

	printWithTime('Response READ')
	return true
end

function server_connection:processResponses()

	if #self.requests > 0 then
		local request
		-- Iterate through the requests in reverse order (they also have been added in reverse order in sendRequest)
		for i = #self.requests, 1, -1 do
			request = self.requests[i]
			if self:getResponseForRequest(request) then
				-- Remove the handled request from the requests table
				table.remove(self.requests, i)
			else
				self.status = 'Waiting for response for ' .. (os.clock() - request.startTime) .. ' seconds'
			end
		end
	end

	if self.nextKeepAlive and os.time() > self.nextKeepAlive then
		self:sendRequest({["$type"] = "KeepAlivePluginRequest"})
	end
end

return server_connection
