using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoLink.Models;

namespace EchoLink.ViewModels;

public abstract class ViewModelBase : ObservableObject 
{
    protected void UpdateDeviceCollection(System.Collections.ObjectModel.ObservableCollection<Device> target, IEnumerable<Device> source)
    {
        var sourceList = source.ToList();
        var toRemove = target.Where(t => !sourceList.Any(s => s.NodeId == t.NodeId)).ToList();
        foreach (var item in toRemove) target.Remove(item);

        foreach (var sourceItem in sourceList)
        {
            var existing = target.FirstOrDefault(t => t.NodeId == sourceItem.NodeId);
            if (existing != null)
            {
                existing.IsOnline = sourceItem.IsOnline;
                existing.LastSeen = sourceItem.LastSeen;
                existing.IpAddress = sourceItem.IpAddress;
                existing.Name = sourceItem.Name;
                existing.DeviceType = sourceItem.DeviceType;
                existing.Os = sourceItem.Os;
                existing.IsSelf = sourceItem.IsSelf;
                existing.IsPaired = sourceItem.IsPaired;
                existing.UserId = sourceItem.UserId;
                existing.Section = sourceItem.Section;
                existing.UpdateStatusLabel();
            }
            else
            {
                target.Add(sourceItem);
            }
        }
    }
}
