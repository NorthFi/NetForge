// root/selfhost/web/warehouse.js
'use strict';
async function warehouseConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Warehouse & stock</h1><p class="sub">Physical balances · serial tracking · installation allocation</p></div><button id="st-refresh">Refresh</button></div><p class="note">Reservations reduce available stock. Issue only after physical handover; serialized items create CRM equipment records. No accounting journal is posted.</p><div id="st-actions"></div><div id="st-main"></div><div id="st-detail"></div>');
 const main=el.querySelector('#st-main'),detail=el.querySelector('#st-detail'),actions=el.querySelector('#st-actions');
 let items=[],warehouses=[],offset=0,view='balances',warehouse='',sequence=0;
 const fail=e=>{if(main.isConnected)notice(e.message,true)};
 const opt=(id,name)=>'<option value="'+esc(id)+'">'+esc(name)+'</option>';
 const itemOptions=()=>items.map(i=>opt(i.id,i.sku+' · '+i.name+(i.serialized?' [serial]':' [bulk]'))).join('');
 const warehouseOptions=()=>warehouses.map(w=>opt(w.id,w.branch+' · '+w.name)).join('');
 // Generate once per dialog, retain across failed requests to prevent replay.
 function form(title,fields,save){
  const requestId=crypto.randomUUID();
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;try{await save(new FormData(e.target),requestId);d.close();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 const stockFields=()=>'<label>Item</label><select name="itemId" required>'+itemOptions()+'</select><label>Source / receiving warehouse</label><select name="warehouseId" required>'+warehouseOptions()+'</select><label>Quantity (integer stock units)</label><input name="quantity" type="number" min="1" max="100000" required><label>Serials (one per line; only for serialized stock)</label><textarea name="serials" rows="5"></textarea>';
 const refField='<label>Document / movement reference</label><input name="reference" required minlength="3" maxlength="240">';
 const movePayload=(f,id)=>({requestId:id,itemId:f.get('itemId'),warehouseId:f.get('warehouseId'),quantity:Number(f.get('quantity')),serials:f.get('serials').split(/\r?\n/).map(s=>s.trim()).filter(Boolean),reference:f.get('reference'),confirm:true});
 async function setup(){
  [items,warehouses]=await Promise.all([api('/stock/items'),api('/stock/warehouses')]);
  if(!main.isConnected)return;
  actions.innerHTML='<div class="buttons">'+(admin?'<button id="st-item">+ Item</button><button id="st-warehouse">+ Warehouse / tech stock</button>':'')+'<button id="st-receive">Receive</button><button id="st-transfer">Transfer</button><button id="st-reserve">Reserve for installation</button></div><div class="buttons"><select id="st-view"><option value="balances">Balances</option><option value="reservations">Reservations</option><option value="movements">Movement history</option><option value="reconciliation">Reconciliation</option><option value="catalogue">Catalogue</option><option value="warehouses">Warehouses</option></select><select id="st-filter"><option value="">All warehouses</option>'+warehouseOptions()+'</select><button id="st-prev">Previous</button><button id="st-next">Next</button></div>';
  actions.querySelector('#st-view').value=view;actions.querySelector('#st-filter').value=warehouse;
  actions.querySelector('#st-view').onchange=e=>{view=e.target.value;offset=0;load().catch(fail)};
  actions.querySelector('#st-filter').onchange=e=>{warehouse=e.target.value;offset=0;load().catch(fail)};
  actions.querySelector('#st-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};
  actions.querySelector('#st-next').onclick=()=>{offset+=100;load().catch(fail)};
  if(admin){
   actions.querySelector('#st-item').onclick=()=>form('Create stock item','<label>SKU</label><input name="sku" required maxlength="80"><label>Name</label><input name="name" required maxlength="160"><label>Model</label><input name="model" maxlength="160"><label>Equipment category</label><select name="category">'+['distribution','backhaul','access','power','cpe','router','other'].map(c=>opt(c,c)).join('')+'</select><label>Unit</label><input name="unit" value="each" maxlength="20" required><label>Tracking</label><select name="serialized"><option value="false">Bulk</option><option value="true">Serialized</option></select><label>Minimum available per stocked warehouse</label><input name="minAvailable" type="number" min="0" max="100000000" value="0" required>',async f=>{await api('/stock/items','POST',{...Object.fromEntries(f),serialized:f.get('serialized')==='true',minAvailable:Number(f.get('minAvailable'))});await setup()});
   actions.querySelector('#st-warehouse').onclick=async()=>{try{
    const branches=await api('/branches');
    const d=dialog('<h2>New warehouse / technician stock</h2><form><label>Branch</label><select name="branchId" required>'+branches.map(b=>opt(b.id,b.name)).join('')+'</select><label>Name</label><input name="name" required maxlength="160"><label>Technician (optional)</label><select name="technicianId"><option value="">Branch warehouse</option></select><p class="error"></p><button class="primary">Create</button><button type="button" id="cancel">Cancel</button></form>');
    const f=d.querySelector('form');let seq=0;
    const update=async()=>{const n=++seq;f.elements.technicianId.innerHTML='<option value="">Branch warehouse</option>';const roster=await api('/installations/roster?branch='+f.elements.branchId.value);if(n===seq&&d.isConnected)f.elements.technicianId.insertAdjacentHTML('beforeend',roster.filter(r=>r.assigned&&r.enabled).map(r=>opt(r.id,r.username)).join(''))};
    f.elements.branchId.onchange=()=>update().catch(fail);d.querySelector('#cancel').onclick=()=>d.close();
    f.onsubmit=async e=>{e.preventDefault();const b=f.querySelector('.primary');b.disabled=true;try{await api('/stock/warehouses','POST',{branchId:f.elements.branchId.value,name:f.elements.name.value,technicianId:f.elements.technicianId.value||null});d.close();await setup();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
    await update();
   }catch(e){fail(e)}};
  }
  actions.querySelector('#st-receive').onclick=()=>form('Receive physical stock',stockFields()+refField,(f,id)=>api('/stock/receive','POST',movePayload(f,id)));
  actions.querySelector('#st-transfer').onclick=()=>form('Transfer unreserved stock',stockFields()+'<label>Destination warehouse</label><select name="destinationId" required>'+warehouseOptions()+'</select>'+refField,(f,id)=>api('/stock/transfer','POST',{...movePayload(f,id),destinationId:f.get('destinationId')}));
  actions.querySelector('#st-reserve').onclick=async()=>{try{
   const orders=await api('/installations/orders?offset=0');
   form('Reserve installation stock','<label>Open installation (first 100 order records)</label><select name="orderId" required>'+orders.filter(o=>!['completed','cancelled'].includes(o.state)).map(o=>opt(o.id,o.number+' · '+o.account+' · '+o.branch)).join('')+'</select>'+stockFields()+refField,(f,id)=>api('/stock/reservations','POST',{...movePayload(f,id),orderId:f.get('orderId')}));
  }catch(e){fail(e)}};
 }
 const table=(headers,rows)=>'<div class="card tablewrap"><table><thead><tr>'+headers.map(h=>'<th>'+esc(h)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+r.map(v=>'<td>'+v+'</td>').join('')+'</tr>').join('')+'</tbody></table></div>';
 const vals=(...x)=>x.map(v=>esc(v??'—'));
 async function serialView(item,w,skip=0){
  const n=++sequence,p=new URLSearchParams({item,offset:String(skip)});if(w)p.set('warehouse',w);
  const rows=await api('/stock/serials?'+p);if(!detail.isConnected||n!==sequence)return;
  detail.innerHTML='<div class="toolbar"><h2>Serial records</h2><button id="st-s-prev">Previous</button><button id="st-s-next">Next</button></div>'+table(['Serial','Warehouse','State','Condition','Equipment ID'],rows.map(r=>vals(r.serial,r.warehouse,r.recovery_state|| (r.equipment_id?'Issued':r.reservation_id?'Reserved':'Available'),r.condition,r.equipment_id)));
  const prev=detail.querySelector('#st-s-prev'),next=detail.querySelector('#st-s-next');prev.disabled=skip===0;next.disabled=rows.length<100;prev.onclick=()=>serialView(item,w,Math.max(0,skip-100)).catch(fail);next.onclick=()=>serialView(item,w,skip+100).catch(fail);
 }
 async function load(){
  const n=++sequence;detail.innerHTML='';
  const paged=['balances','reservations','movements','reconciliation'].includes(view);let rows=[];
  if(paged){const p=new URLSearchParams({offset:String(offset)});if(view==='balances'&&warehouse)p.set('warehouse',warehouse);rows=await api('/stock/'+view+'?'+p)}else rows=view==='catalogue'?items:warehouses;
  if(!main.isConnected||n!==sequence)return;
  actions.querySelector('#st-filter').disabled=view!=='balances';actions.querySelector('#st-prev').disabled=!paged||offset===0;actions.querySelector('#st-next').disabled=!paged||rows.length<100;
  if(view==='balances'){
   main.innerHTML=table(['Warehouse','SKU','On hand','Reserved','Available','Minimum','Status',''],rows.map(r=>[...vals(r.warehouse,r.sku,r.on_hand,r.reserved,r.available,r.min_available,r.low_stock?'Low':'OK'),r.serialized?'<button data-serial="'+esc(r.item_id)+'" data-wh="'+esc(r.warehouse_id)+'">Serials</button>':'']));
  }else if(view==='reservations'){
   main.innerHTML=table(['Installation','SKU','Warehouse','Qty','State','Serials',''],rows.map(r=>[...vals(r.order_number,r.sku,r.warehouse,r.quantity,r.state,(r.serials||[]).join(', ')),r.state==='reserved'?'<button data-release="'+esc(r.id)+'">Release</button><button data-issue="'+esc(r.id)+'">Issue</button>':'']));
  }else if(view==='movements'){
   main.innerHTML=table(['Time','Action','SKU','From','To','Qty','Serials','Reference','Actor'],rows.map(r=>vals(r.created_at,r.action,r.sku,r.from_name,r.to_name,r.quantity,(r.serials||[]).join(', '),r.reference,r.username)));
  }else if(view==='reconciliation'){
   main.innerHTML='<p class="note">'+(rows.length?'Discrepancies detected. Investigate; no automatic repair.':offset===0?'No balance discrepancies found in the current database snapshot.':'No further discrepancies.')+'</p>'+table(['Warehouse','SKU','On hand','Ledger on hand','Reserved','Reservation total','Serial on hand','Serial reserved'],rows.map(r=>vals(r.warehouse,r.sku,r.on_hand,r.ledger_on_hand,r.reserved,r.reservation_total,r.serial_on_hand,r.serial_reserved)));
  }else if(view==='catalogue'){
   main.innerHTML=table(['SKU','Name','Model','Unit','Tracking','Minimum',''],rows.map(r=>[...vals(r.sku,r.name,r.model,r.unit,r.serialized?'Serialized':'Bulk',r.min_available),r.serialized?'<button data-serial="'+esc(r.id)+'">Serials</button>':'']));
  }else main.innerHTML=table(['Warehouse','Branch','Technician'],rows.map(r=>vals(r.name,r.branch,r.technician)));
  main.querySelectorAll('[data-serial]').forEach(b=>b.onclick=()=>serialView(b.dataset.serial,b.dataset.wh).catch(fail));
  for(const action of ['release','issue'])main.querySelectorAll('[data-'+action+']').forEach(b=>b.onclick=()=>form(action==='issue'?'Issue to installation customer':'Release stock reservation','<p>'+(action==='issue'?'Confirm actual physical deployment. This creates customer equipment for serialized units and deducts stock.':'Return reserved stock to available balance without changing on-hand.')+'</p>'+refField,(f,id)=>api('/stock/reservations/'+b.dataset[action]+'/'+action,'POST',{requestId:id,reference:f.get('reference'),confirm:true})));
 }
 el.querySelector('#st-refresh').onclick=()=>setup().then(load).catch(fail);
 try{await setup();await load()}catch(e){fail(e)}
}
