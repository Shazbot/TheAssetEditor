

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.



local function new_game_startup()

local function spawn_new_force()

    local batman_faction_string = "wh2_main_hef_yvresse"
    local batman_faction = cm:get_faction(batman_faction_string)
    
    if batman_faction:is_human() then --since player Eltharion spawns right by Gronti Mingol, we make an exception for Gashnag
    
        cm:create_force_with_general(
            "mixer_teb_gashnag", -- faction_key,
            "teb_shieldbearers,teb_border_rangers,teb_border_rangers,teb_guard_kossars,teb_billmen,teb_billmen,teb_light_scouts,teb_light_scouts", 
            "wh3_main_combi_region_galbaraz", -- region_key,
                569,
                459, --by Vardanos instead
            "general", -- type,
            "teb_gashnag", -- subtype,
            "names_name_997012",    -- first name
            "",
            "names_name_997013",    -- last name
            "", -- name4,
            true,-- make_faction_leader,
            function(cqi) -- callback
                local str = "character_cqi:" .. cqi
                cm:set_character_immortality(str, true);
                cm:set_character_unique(str, true);
            end
        )

        cm:create_agent(
            "mixer_teb_gashnag",
            "dignitary",
            "teb_priestess",
                575,
                465,
            false
        )
    else 

        cm:create_force_with_general(
            "mixer_teb_gashnag", -- faction_key,
            "teb_shieldbearers,teb_border_rangers,teb_border_rangers,teb_guard_kossars,teb_billmen,teb_billmen,teb_light_scouts,teb_light_scouts", 
            "wh3_main_combi_region_galbaraz", -- region_key,
            608, -- x,
            392, -- y,
            "general", -- type,
            "teb_gashnag", -- subtype,
            "names_name_997012",    -- first name
            "",
            "names_name_997013",    -- last name
            "", -- name4,
            true,-- make_faction_leader,
            function(cqi) -- callback
                local str = "character_cqi:" .. cqi
                cm:set_character_immortality(str, true);
                cm:set_character_unique(str, true);
            end
        )
        
        cm:transfer_region_to_faction("wh3_main_combi_region_gronti_mingol", "mixer_teb_gashnag")     

        cm:create_agent(
            "mixer_teb_gashnag",
            "dignitary",
            "teb_priestess",
            613, -- x,
            387, -- y,
            false
        )
    end --of the if batman
end
--EVERYTHING HEREAFTER HAPPENS REGARDLESS OF BATMAN


    local teb_faction_string = "mixer_teb_gashnag"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()


    --local galbaraz = cm:get_region("wh3_main_combi_region_galbaraz")

   
    cm:transfer_region_to_faction("wh3_main_combi_region_verdanos", "mixer_teb_gashnag")

    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end

    local unit_count = 1 -- card32 count
    local rcp = 20 -- float32 replenishment_chance_percentage
    local max_units = 1 -- int32 max_units
    local murpt = 0.1 -- float32 max_units_replenished_per_turn
    local xp_level = 0 -- card32 xp_level
    local frr = "" -- (may be empty) String faction_restricted_record
    local srr = "" -- (may be empty) String subculture_restricted_record
    local trr = "" -- (may be empty) String tech_restricted_record
    local units = {
                    "teb_ricco",
                    "teb_alcatani",
                    "teb_pirazzo",
                    "teb_leopard",
                    "teb_venators",
                    "teb_vespero",
                    "teb_marksmen",
                    "teb_besiegers",
           --         "teb_muktar",
            --        "teb_amazons",
                    "teb_cursed",
                    "teb_origo",
                    "teb_manflayers",
                    "teb_asarnil",
                    "teb_tichi",
                    --"teb_bronzino_limbered",
                    "teb_bronzino",
                    "teb_birdmen", --MIND THE COMMA IF ROC ARE INCLUDED
--ROCs 

                        "teb_roc_dieterfist",
                        "teb_roc_estragon",
                        "teb_roc_verena",
                        "teb_roc_strygos",
                        "teb_roc_broswords",
                        "teb_roc_gwatch",
                        "teb_roc_kotss",
                        "teb_roc_organ",
                        "teb_roc_gunn",
                        "teb_roc_montecastello",
                        "teb_tank_ror"
        
    }
  

    cm:create_force_with_general(
				"wh3_main_ogr_bloodmaw",
                "wh3_main_ogr_inf_gnoblars_0,wh3_main_ogr_inf_gnoblars_0,wh3_main_ogr_inf_gnoblars_0,wh3_main_ogr_inf_maneaters_0,wh3_main_ogr_inf_maneaters_0,wh3_main_ogr_inf_ogres_0,wh3_main_ogr_inf_ogres_0",
				"wh3_main_combi_region_verdanos",
                563,
                451,
                "general", -- type,
                "wh3_main_ogr_tyrant", -- subtype,
                "names_name_497110579",	-- first name
                "",
                "names_name_1243788743",	-- last name
                "", -- name4,
				false,
				function(cqi)
					cm:apply_effect_bundle_to_characters_force("wh_main_bundle_military_upkeep_free_force", cqi, -1, true)     --not really necessary that they are immobile and free, since you're nowhere near to intervene, but if I remove this stuff it breaks.
					cm:disable_movement_for_character("character_cqi:" .. cqi)

				end
			)

        cm:transfer_region_to_faction("wh3_main_combi_region_zvorak", "wh3_main_ogr_bloodmaw")
			--EXCEPTION: IF OVN DREAD KING IS UP (because they have a minor faction ), we leave Myrmidens and Argalis alone.
-- if not vfs.exists("script/campaign/wh3_main_combi/mod/ovn_dread_king.lua") then
if not OVN_DREAD_KING_MINOR or not OVN_DREAD_KING_MINOR.spawn_italy then
        cm:transfer_region_to_faction("wh3_main_combi_region_argalis", "wh3_main_ogr_bloodmaw")
        cm:transfer_region_to_faction("wh3_main_combi_region_myrmidens", "wh3_main_ogr_bloodmaw")
end
        cm:force_declare_war("wh3_main_ogr_bloodmaw", "mixer_teb_gashnag", false, false)
        cm:force_declare_war("wh_main_grn_top_knotz", "mixer_teb_gashnag", false, false)
        cm:force_declare_war("wh3_main_ie_vmp_sires_of_mourkain", "mixer_teb_gashnag", false, false)



    for _, unit in ipairs(units) do
        cm:add_unit_to_faction_mercenary_pool(
            teb_faction,
            unit,
            "wh3_main_regiments_of_renown_pool",
            unit_count,
            rcp,
            max_units,
            murpt,
            frr,
            srr,
            trr,
            true,
            unit
        )
    end
    
    cm:callback(function()
        if to_kill_cqi then
            local str = "character_cqi:" .. to_kill_cqi
            cm:set_character_immortality(str, false)
            cm:kill_character_and_commanded_unit(str, true)
        end
    end, 0)

        cm:callback(
        function()
            cm:show_message_event(
                teb_faction_string,
                "event_feed_strings_text_wh2_scripted_event_how_they_play_title",
                "factions_screen_name_" .. teb_faction_string,
                "event_feed_strings_text_teb_how_they_play",
                true,
                3011
            );
        end,
        1
    )           
end



cm:add_first_tick_callback(
    function()
        local ok, err = pcall(function()
            mixer_set_faction_trait("mixer_teb_gashnag", "teb_lord_trait_teb_gashnag", true)
        end) if not ok then script_error(err) end
        if cm:is_new_game() then
            if cm:get_campaign_name() == "main_warhammer" then
                local ok, err =
                    pcall(
                    function()
                        new_game_startup()
                    end
                )
                if not ok then
                    script_error(err)
                end
            end
        end
    end
)