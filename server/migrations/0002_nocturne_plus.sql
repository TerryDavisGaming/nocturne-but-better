-- nocturne+ hub: the mod was renamed from nocturne but better. a hub that still has the old default name
-- gets the new one; a name the owner typed on /admin stays as it is.
UPDATE settings SET v = 'nocturne+ hub' WHERE k = 'hub_name' AND v = 'nocturne but better hub';
