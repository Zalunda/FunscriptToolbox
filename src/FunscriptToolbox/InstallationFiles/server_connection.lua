-- FunscriptToolbox.MotionVectors.UI LUA server_connection
json = require "json"

local server_connection = {}

function server_connection:new(FTMVSFullPath, enableLogs)
	local o = { 
		FTMVSFullPath = FTMVSFullPath,
		enableLogs = enableLogs,
		serverProcessHandle = nil,
		requests = {}
	}
	setmetatable(o, {__index = server_connection})
	return o
end

-- Find an available 'channel' to communicate with the server (in case multiple OFS are running at the same time)
-- The function open a '.lock' file, which is not closed. This will 'reserve' that channel for this process. 
function server_connection:setBaseCommunicationFilePath()
    while true do
        local randomId = math.random(1,1000)
		randomId = 99 -- TODO REMOVE
        self.channelBaseFilePath = ofs.ExtensionDir() .. "\\Channel-" .. randomId

        local success, file = pcall(io.open, self.channelBaseFilePath .. ".lock", "w+")
        if not success then
            print("Channel file already used: " .. file)
        else
			print('Channel '.. randomId .. ' locked for this process.')
            self.channelBaseFileLock = file
			self.channelCurrentTransactionNumber = 1
			return
        end
    end
end

function server_connection:startServerIfNeeded()

    if self.serverProcessHandle and self.serverProcessHandle:alive() then
        return
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
    table.insert(args, self.channelBaseFilePath .. "-")
    table.insert(args, "--timeout")
    table.insert(args, 300)

    print("cmd: ", cmd)
    print("args: ", table.unpack(args))

    self.serverProcessHandle = Process.new(cmd, table.unpack(args))
end

function server_connection:sendRequest(request, callback)

	self:startServerIfNeeded()

	local transactionNumber = self.channelCurrentTransactionNumber
	self.channelCurrentTransactionNumber = self.channelCurrentTransactionNumber + 1
	
	local requestFilePath = self.channelBaseFilePath .. '-' .. transactionNumber .. ".json"
	local responseFilePath = self.channelBaseFilePath .. '-' .. transactionNumber .. ".response.json"
	
    print('Sending request #' .. transactionNumber)
	local encoded_data = json.encode(request)
 	local requestFile = io.open(requestFilePath, "w")
 	requestFile:write(encoded_data)
 	requestFile:close()
	
    table.insert(self.requests, { 
		transactionNumber = transactionNumber, 
		request = request, 
		responseFilePath = responseFilePath, 
		callback = callback 
	})
end

function server_connection:getResponseForRequest(request)

	local f = io.open(request.responseFilePath)
    if not f then
        return nil
    end

	print('Reading response #' .. request.transactionNumber)
    local content = f:read("*a")
    f:close()
	print(content)
    response_body = json.decode(content)
	os.remove(request.responseFilePath)
	
	return response_body	
end

function server_connection:processResponses()
	-- Iterate through the requests in reverse order
	for i = #self.requests, 1, -1 do
		local request = self.requests[i]
		local response = self:getResponseForRequest(request)
		if response then
			-- Call the callback with the response
			request.callback(response)
			-- Remove the handled request from the requests table
			table.remove(self.requests, i)
		end
	end
	
	-- TODO add keepalive	
end

return server_connection
