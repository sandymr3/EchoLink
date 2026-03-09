package main

/*
#include <stdlib.h>
#include <string.h>
*/
import "C"
import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"sync"

	"github.com/pkg/sftp"
	"golang.org/x/crypto/ssh"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tsnet"
)

var (
	tsServer *tsnet.Server
	sshLn    net.Listener
	mu       sync.Mutex
)

//export StartEchoLinkNode
func StartEchoLinkNode(configDir *C.char, authKey *C.char, hostname *C.char) int {
	mu.Lock()
	defer mu.Unlock()

	if tsServer != nil {
		return 0
	}

	conf := C.GoString(configDir)
	host := C.GoString(hostname)
	key  := C.GoString(authKey)

	if host == "" {
		host = "echolink-android"
	}

	log.Printf("[Go] Starting node: Host=%s, Dir=%s", host, conf)

	tsServer = &tsnet.Server{
		Dir:          conf,
		Hostname:     host,
		AuthKey:      key,
		ControlURL:   "https://echo-link.app",
		Logf: func(format string, args ...any) {
			log.Printf("[tsnet] "+format, args...)
		},
	}

	go func() {
		// Up() is preferred for tsnet to ensure initialization
		_, err := tsServer.Up(context.Background())
		if err == nil {
			startSftpServer()
		} else {
			log.Printf("[Go] tsServer.Up error: %v", err)
		}
	}()

	return 1
}

func startSftpServer() {
	mu.Lock()
	if sshLn != nil {
		mu.Unlock()
		return
	}
	mu.Unlock()

	ln, err := tsServer.Listen("tcp", ":2222")
	if err != nil {
		log.Printf("[Go] Failed to listen on :2222: %v", err)
		return
	}
	sshLn = ln

	config := &ssh.ServerConfig{
		NoClientAuth: true,
	}

	log.Printf("[Go] SFTP Server listening on :2222")

	for {
		conn, err := sshLn.Accept()
		if err != nil {
			return
		}
		go handleSshConn(conn, config)
	}
}

func handleSshConn(nConn net.Conn, config *ssh.ServerConfig) {
	_, chans, reqs, err := ssh.NewServerConn(nConn, config)
	if err != nil {
		return
	}
	go ssh.DiscardRequests(reqs)

	for newChannel := range chans {
		if newChannel.ChannelType() != "session" {
			newChannel.Reject(ssh.UnknownChannelType, "unknown channel type")
			continue
		}
		channel, requests, _ := newChannel.Accept()

		go func(in <-chan *ssh.Request) {
			for req := range in {
				if req.Type == "subsystem" && string(req.Payload[4:]) == "sftp" {
					req.Reply(true, nil)
					server := sftp.NewRequestServer(channel, sftp.InMemHandler())
					if err := server.Serve(); err != nil && err != io.EOF {
						log.Print("[Go] SFTP error:", err)
					}
					return
				}
				req.Reply(false, nil)
			}
		}(requests)
	}
}

type Device struct {
	Name      string `json:"Name"`
	IpAddress string `json:"IpAddress"`
	IsOnline  bool   `json:"IsOnline"`
	DeviceType string `json:"DeviceType"`
	Os         string `json:"Os"`
}

func getStatus() (*ipnstate.Status, error) {
	if tsServer == nil {
		return nil, fmt.Errorf("not started")
	}
	lc, err := tsServer.LocalClient()
	if err != nil {
		return nil, err
	}
	return lc.Status(context.Background())
}

//export GetPeerListJson
func GetPeerListJson() *C.char {
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("[]")
	}

	var devices []Device
	for _, peer := range status.Peer {
		ip := ""
		if len(peer.TailscaleIPs) > 0 {
			ip = peer.TailscaleIPs[0].String()
		}

		devices = append(devices, Device{
			Name:       peer.HostName,
			IpAddress:  ip,
			IsOnline:   peer.Online,
			DeviceType: "Desktop",
			Os:         peer.OS,
		})
	}

	data, _ := json.Marshal(devices)
	return C.CString(string(data))
}

//export GetBackendState
func GetBackendState() *C.char {
	status, err := getStatus()
	if err != nil {
		return C.CString("NotStarted")
	}

	// Logic: If we have an IP, we are Running.
	// If we have an AuthURL, we definitely need login.
	if len(status.TailscaleIPs) > 0 && status.BackendState == "Running" {
		return C.CString("Running")
	}
	
	if status.AuthURL != "" || status.BackendState == "NeedsLogin" {
		return C.CString("NeedsLogin")
	}

	return C.CString(status.BackendState)
}

//export GetTailscaleIp
func GetTailscaleIp() *C.char {
	status, err := getStatus()
	if err != nil || status == nil || len(status.TailscaleIPs) == 0 {
		return C.CString("")
	}
	return C.CString(status.TailscaleIPs[0].String())
}

//export GetLoginUrl
func GetLoginUrl() *C.char {
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("")
	}
	return C.CString(status.AuthURL)
}

//export LogoutNode
func LogoutNode() {
	mu.Lock()
	defer mu.Unlock()
	if tsServer == nil {
		return
	}
	lc, err := tsServer.LocalClient()
	if err == nil {
		log.Printf("[Go] Triggering Logout...")
		lc.Logout(context.Background())
	}
}

//export StopEchoLinkNode
func StopEchoLinkNode() {
	mu.Lock()
	defer mu.Unlock()
	if sshLn != nil {
		sshLn.Close()
		sshLn = nil
	}
	if tsServer != nil {
		tsServer.Close()
		tsServer = nil
	}
}

func main() {}
