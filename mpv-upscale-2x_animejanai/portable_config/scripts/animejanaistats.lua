local open = io.open

local showingMessage = false
local MAX_DURATION = 2147483

-- Function to be called when the custom action is triggered
function show_animejanai_stats()
    if showingMessage then
        mp.osd_message("")  -- Clear the OSD message
        showingMessage = false
    else
        local message = ""

        if mp.get_property("vf") == "" then
            message = "Upscaling is disabled"
        else
            local data_file_path = (mp.command_native({'expand-path', "~~/../animejanai/core/currentanimejanai.log"}))
            message = read_file(data_file_path)

            if message == "" then
                message = "Error during upscale; press ~ to view error in console"
            end
        end
        
        mp.osd_message(message, MAX_DURATION)
        showingMessage = true
    end
end

function read_file(path)
    local file = open(path, "r") -- r read mode and b binary mode
    if not file then return nil end
    local content = file:read "*a" -- *a or *all reads the whole file
    file:close()
    return content
end


-- Register the custom action to a key binding (e.g., "Ctrl+Shift+C")
mp.add_key_binding("Ctrl+j", "show_animejanai_stats", show_animejanai_stats)